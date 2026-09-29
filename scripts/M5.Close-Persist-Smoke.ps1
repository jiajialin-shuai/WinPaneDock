$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$exe = Join-Path $root 'spikes/M0.Terminal.Wpf/bin/Release/net8.0-windows/Cmux.Spike.Terminal.exe'
Add-Type -Path $core
$instanceId = "close-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$hostPipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$guiPipeName = [Cmux.Core.InstanceScope]::Qualify('cmux-gui-' + [Environment]::UserName)
$layoutPath = Join-Path $env:TEMP ("cmux-close-smoke-{0}.json" -f [guid]::NewGuid())
$app = $null
$hostProcessId = $null
$sessionId = $null

function Send-Request([string]$pipeName, $request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null
    $writer = $null
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait(5000)) { throw 'IPC response timed out.' }
        return $task.Result | ConvertFrom-Json
    }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

try {
    $manager = [Cmux.Core.WorkspaceManager]::new()
    $workspace = $manager.Create('Close', $root)
    $tab = $manager.CreateTab($workspace.Id, 'Shell')
    $sessionId = $tab.RootPane.SessionId.ToString()
    $manager.SetTerminalLaunch($workspace.Id, $tab.Id, $tab.RootPane.SessionId,
        'CMD', 'cmd.exe /k', $root, 'CMD')
    [Cmux.Core.LayoutStore]::new($layoutPath).Save($manager.Export())
    $arguments = "--layout-path `"$layoutPath`" --instance-id `"$instanceId`""

    $app = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $identity = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $identity = Send-Request $hostPipeName @{ Command = 'identity'; ProtocolVersion = 2 }; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $identity) { throw 'Isolated SessionHost did not start.' }
    if ($identity.Identity.InstanceId -ne $instanceId -or
        [IO.Path]::GetFullPath($identity.Identity.ExecutablePath) -ne [IO.Path]::GetFullPath((Join-Path $root 'spikes/M0.Terminal.Wpf/bin/Release/net8.0-windows/Cmux.SessionHost.exe'))) {
        throw 'Isolated SessionHost identity did not match the test build.'
    }
    $hostProcessId = $identity.Identity.ProcessId
    $before = Send-Request $hostPipeName @{ Command = 'list'; ProtocolVersion = 2 }
    if (@($before.Sessions | Where-Object SessionId -eq $sessionId).Count -ne 1) { throw 'Test session was not created.' }

    $close = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $close = Send-Request $guiPipeName @{ Command = 'close-pane' }; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $close -or -not $close.Ok) { throw "GUI close command failed: $($close.Message)" }
    Stop-Process -Id $app.Id -Force
    $app.WaitForExit()
    if ((Get-Content -LiteralPath $layoutPath -Raw) -match 'cmd\.exe') { throw 'Close intent was not persisted before the acknowledgement.' }

    $app = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    $after = Send-Request $hostPipeName @{ Command = 'list'; ProtocolVersion = 2 }
    if (@($after.Sessions | Where-Object SessionId -eq $sessionId).Count -ne 0) {
        throw 'Explicitly closed terminal was resurrected after GUI restart.'
    }
    Write-Host "Explicit close persistence PASS: isolated host $hostProcessId, session $sessionId was not resurrected."
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($file in @($layoutPath, "$layoutPath.bak", "$layoutPath.tmp", "$layoutPath.bak.tmp")) {
        Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
