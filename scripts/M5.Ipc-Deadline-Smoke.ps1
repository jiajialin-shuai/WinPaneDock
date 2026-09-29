$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$hostExe = Join-Path $root 'src/Cmux.SessionHost/bin/Release/net8.0-windows/Cmux.SessionHost.exe'
Add-Type -Path $core
$instanceId = "ipc-smoke-$([guid]::NewGuid().ToString('N'))"
$previous = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify('cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$hostProcess = $null
$idle = $null

function Send-HostRequest($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null; $writer = $null
    try {
        $pipe.Connect(5000); $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true; $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $task = $reader.ReadLineAsync(); if (-not $task.Wait(5000)) { throw 'response timeout' }
        $result = $task.Result | ConvertFrom-Json; if (-not $result.Ok) { throw $result.Error }; return $result
    }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

try {
    $hostProcess = Start-Process $hostExe -WindowStyle Hidden -PassThru
    $identity = $null
    for ($i = 0; $i -lt 50 -and -not $identity; $i++) { try { $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 } } catch { Start-Sleep -Milliseconds 100 } }
    if (-not $identity) { throw 'host did not start' }
    $idle = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $idle.Connect(5000)
    $idleWriter = [IO.StreamWriter]::new($idle); $idleWriter.AutoFlush = $true
    $idleReader = [IO.StreamReader]::new($idle)
    $idleWriter.Write('{"Command":"list"') # deliberately no newline
    Start-Sleep -Seconds 6
    $closedOnWrite = $false
    try { $idleWriter.WriteLine('') } catch { $closedOnWrite = $true }
    if (-not $closedOnWrite) {
        $lateResponse = $idleReader.ReadLineAsync()
        $lateCompleted = $lateResponse.Wait(1500)
        if ($lateCompleted -and $lateResponse.Result) { throw 'Server accepted a request after its first-line deadline.' }
    }
    $responsive = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
    if ($responsive.Identity.ProcessId -ne $hostProcess.Id) { throw 'host identity changed' }
    Write-Host "IPC deadline PASS: idle client was released and host remained responsive (PID $($hostProcess.Id))."
}
finally {
    try { if ($idleReader) { $idleReader.Dispose() } } catch { }
    try { if ($idleWriter) { $idleWriter.Dispose() } } catch { }
    try { if ($idle) { $idle.Dispose() } } catch { }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previous) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previous }
}
