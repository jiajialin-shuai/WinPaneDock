$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$hostExe = Join-Path $root 'src/Cmux.SessionHost/bin/Release/net8.0-windows/Cmux.SessionHost.exe'
Add-Type -Path $core
$instanceId = "lease-smoke-$([guid]::NewGuid().ToString('N'))"
$previous = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify('cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$hostProcess = $null
$sessionId = [guid]::NewGuid().ToString('N')

function Send-Raw($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null; $writer = $null
    try {
        $pipe.Connect(5000); $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true; $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 8))
        $task = $reader.ReadLineAsync(); if (-not $task.Wait(5000)) { throw 'response timeout' }
        return $task.Result | ConvertFrom-Json
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
    for ($i = 0; $i -lt 50 -and -not $identity; $i++) { try { $identity = Send-Raw @{ Command = 'identity'; ProtocolVersion = 2 } } catch { Start-Sleep -Milliseconds 100 } }
    if (-not $identity) { throw 'host did not start' }
    $first = Send-Raw @{ Command = 'create'; ProtocolVersion = 2; SessionId = $sessionId; CommandLine = 'cmd.exe /k'; WorkingDirectory = $PSScriptRoot }
    if (-not $first.Ok -or [string]::IsNullOrWhiteSpace($first.LeaseId)) { throw 'create did not return a lease' }
    $oldLease = $first.LeaseId
    Send-Raw @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $oldLease } | Out-Null
    $second = Send-Raw @{ Command = 'create'; ProtocolVersion = 2; SessionId = $sessionId; CommandLine = 'cmd.exe /k'; WorkingDirectory = $PSScriptRoot }
    if (-not $second.Ok -or $second.LeaseId -eq $oldLease) { throw 'replacement session did not receive a new lease' }
    $stale = Send-Raw @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $oldLease; Data = "echo CMUX_STALE`r" }
    if ($stale.Ok) { throw 'stale lease input was accepted.' }
    $valid = Send-Raw @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $second.LeaseId; Data = "echo CMUX_CURRENT`r" }
    if (-not $valid.Ok) { throw 'current lease input was rejected.' }
    $replay = $null
    for ($attempt = 0; $attempt -lt 10 -and -not $replay; $attempt++) {
        Start-Sleep -Milliseconds 500
        $candidate = Send-Raw @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $second.LeaseId }
        if ($candidate.Replay -match 'CMUX_CURRENT') { $replay = $candidate }
    }
    if (-not $replay -or $replay.Replay -match 'CMUX_STALE') { throw 'lease fence did not isolate stale input.' }
    Write-Host "Session lease fence PASS: stale input was rejected and current lease remained usable (host $($hostProcess.Id))."
}
finally {
    try { Send-Raw @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId } | Out-Null } catch { }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previous) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previous }
}
