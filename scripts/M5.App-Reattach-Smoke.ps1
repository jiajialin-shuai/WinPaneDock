param([switch]$Crash, [int]$DetachedSeconds = 30)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
Add-Type -Path $core
$exe = Join-Path $root 'spikes/M0.Terminal.Wpf/bin/Release/net8.0-windows/Cmux.Spike.Terminal.exe'
$layoutPath = Join-Path $env:TEMP ("cmux-m5-{0}.json" -f [guid]::NewGuid())
$pipeName = 'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_')
$previousHosts = @(Get-CimInstance Win32_Process -Filter "Name='Cmux.SessionHost.exe'" | ForEach-Object ProcessId)
$app = $null
$sessionId = $null

function Send-HostRequest($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
    }
    finally { $pipe.Dispose() }
}

function Stop-App([switch]$Force) {
    if ($app -and -not $app.HasExited) {
        if ($Force) {
            Stop-Process -Id $app.Id -Force
            $app.WaitForExit()
            return
        }
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { throw 'App did not close cleanly.' }
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

    $app = Start-Process -FilePath $exe -ArgumentList "--layout-path `"$layoutPath`"" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    if ($app.HasExited) { throw "App exited early: $($app.ExitCode)" }
    $first = Send-HostRequest @{ Command = 'attach'; SessionId = $sessionId }
    $pidBefore = $first.ProcessId
    $lengthBefore = $first.Replay.Length
    if ($pidBefore -le 0 -or $lengthBefore -le 0) { throw 'Initial ping session has no PID or output.' }

    Stop-App -Force:$Crash
    Start-Sleep -Seconds $DetachedSeconds
    $detached = Send-HostRequest @{ Command = 'attach'; SessionId = $sessionId }
    if ($detached.ProcessId -ne $pidBefore -or $detached.Replay.Length -le $lengthBefore + 100) {
        throw "Detached ping did not continue: PID $pidBefore -> $($detached.ProcessId), replay $lengthBefore -> $($detached.Replay.Length)."
    }

    $app = Start-Process -FilePath $exe -ArgumentList "--layout-path `"$layoutPath`"" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 5
    if ($app.HasExited) { throw "Reopened app exited early: $($app.ExitCode)" }
    $reattached = Send-HostRequest @{ Command = 'list' }
    $same = @($reattached.Sessions | Where-Object SessionId -eq $sessionId)
    if ($same.Count -ne 1 -or $same[0].ProcessId -ne $pidBefore) {
        throw 'Reopened app did not preserve the shell PID.'
    }
    Write-Host "M5 app reattach PASS: shell PID $pidBefore, ${DetachedSeconds}s detached, crash=$Crash, replay $lengthBefore -> $($detached.Replay.Length) chars."
}
finally {
    try { Stop-App } catch { Write-Warning $_ }
    if ($sessionId) { try { Send-HostRequest @{ Command = 'close'; SessionId = $sessionId } | Out-Null } catch { } }
    foreach ($file in @($layoutPath, "$layoutPath.bak", "$layoutPath.tmp", "$layoutPath.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
    $newHosts = @(Get-CimInstance Win32_Process -Filter "Name='Cmux.SessionHost.exe'" |
        Where-Object { $_.ProcessId -notin $previousHosts } | ForEach-Object ProcessId)
    foreach ($processId in $newHosts) { Stop-Process -Id $processId -Force }
}
