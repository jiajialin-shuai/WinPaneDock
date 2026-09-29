param([switch]$Crash, [int]$DetachedSeconds = 30, [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root "src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
Add-Type -Path $core
$exe = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.Spike.Terminal.exe"
$sessionHostExe = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.SessionHost.exe"
$layoutPath = Join-Path $env:TEMP ("cmux-m5-{0}.json" -f [guid]::NewGuid())
$instanceId = "app-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$app = $null
$hostProcessId = $null
$sessionId = $null
$leaseId = $null

function Send-HostRequest($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
        $lineTask = $reader.ReadLineAsync()
        if (-not $lineTask.Wait(5000)) { throw 'SessionHost response timed out.' }
        $response = $lineTask.Result | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
    }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

function Stop-App([switch]$Force) {
    if ($app -and -not $app.HasExited) {
        if ($Force) {
            Stop-Process -Id $app.Id -Force
            $app.WaitForExit()
            return
        }
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) {
            Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
            $app.WaitForExit()
            throw 'App did not close cleanly.'
        }
    }
}

try {
    $manager = [Cmux.Core.WorkspaceManager]::new()
    $workspace = $manager.Create('Continuity', $root)
    $tab = $manager.CreateTab($workspace.Id, 'Ping')
    $sessionId = $tab.RootPane.SessionId.ToString()
    $manager.SetTerminalLaunch($workspace.Id, $tab.Id, $tab.RootPane.SessionId,
        'CMD', 'cmd.exe /k ping -t localhost', $root, 'CMD')
    [Cmux.Core.LayoutStore]::new($layoutPath).Save($manager.Export())

    $arguments = "--layout-path `"$layoutPath`" --instance-id `"$instanceId`""
    $app = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    if ($app.HasExited) { throw "App exited early: $($app.ExitCode)" }
    $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
    if ($identity.Identity.InstanceId -ne $instanceId -or
        [IO.Path]::GetFullPath($identity.Identity.ExecutablePath) -ne [IO.Path]::GetFullPath($sessionHostExe)) {
        throw 'The app did not connect to the expected isolated SessionHost.'
    }
    $hostProcessId = $identity.Identity.ProcessId
    $listed = Send-HostRequest @{ Command = 'list'; ProtocolVersion = 2 }
    $leaseId = @($listed.Sessions | Where-Object SessionId -eq $sessionId)[0].LeaseId
    if ([string]::IsNullOrWhiteSpace($leaseId)) { throw 'Session lease was not returned.' }
    $first = Send-HostRequest @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId }
    $pidBefore = $first.ProcessId
    $lengthBefore = $first.Replay.Length
    if ($pidBefore -le 0 -or $lengthBefore -le 0) { throw 'Initial ping session has no PID or output.' }

    Stop-App -Force:$Crash
    Start-Sleep -Seconds $DetachedSeconds
    $detached = Send-HostRequest @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId }
    $minimumGrowth = [Math]::Max(20, $DetachedSeconds * 10)
    if ($detached.ProcessId -ne $pidBefore -or $detached.Replay.Length -le $lengthBefore + $minimumGrowth) {
        throw "Detached ping did not continue: PID $pidBefore -> $($detached.ProcessId), replay $lengthBefore -> $($detached.Replay.Length)."
    }

    $app = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    if ($app.HasExited) { throw "Reopened app exited early: $($app.ExitCode)" }
    $identityAfterRestart = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
    if ($identityAfterRestart.Identity.ProcessId -ne $hostProcessId) {
        throw 'Reopened app connected to a different isolated SessionHost.'
    }
    $reattached = Send-HostRequest @{ Command = 'list'; ProtocolVersion = 2 }
    $same = @($reattached.Sessions | Where-Object SessionId -eq $sessionId)
    if ($same.Count -ne 1 -or $same[0].ProcessId -ne $pidBefore) {
        throw 'Reopened app did not preserve the shell PID.'
    }
    Write-Host "M5 app reattach PASS: isolated host PID $hostProcessId, shell PID $pidBefore, ${DetachedSeconds}s detached, crash=$Crash, replay $lengthBefore -> $($detached.Replay.Length) chars."
}
finally {
    try { Stop-App } catch { Write-Warning $_ }
    if ($sessionId) { try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | Out-Null } catch { } }
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($file in @($layoutPath, "$layoutPath.bak", "$layoutPath.tmp", "$layoutPath.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue }
    else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
