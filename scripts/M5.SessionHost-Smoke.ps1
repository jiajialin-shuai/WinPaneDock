$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$hostExe = Join-Path $root 'src/Cmux.SessionHost/bin/Release/net8.0-windows/Cmux.SessionHost.exe'
Add-Type -Path $core
$instanceId = "smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$hostProcess = $null
$sessionId = [guid]::NewGuid().ToString('N')
$exitId = [guid]::NewGuid().ToString('N')
$leaseId = $null
$exitLeaseId = $null

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
        $line = $lineTask.Result
        if (-not $line) { throw 'SessionHost returned no response.' }
        $result = $line | ConvertFrom-Json
        if (-not $result.Ok) { throw $result.Error }
        return $result
    }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

try {
    $hostProcess = Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru
    $identity = $null
    $lastIdentityError = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
            break
        } catch { $lastIdentityError = $_; Start-Sleep -Milliseconds 100 }
    }
    if (-not $identity -or $identity.Identity.ProcessId -ne $hostProcess.Id) {
        Write-Host "Last identity error: $($lastIdentityError | Out-String)"
        Write-Host "Identity response: $($identity | ConvertTo-Json -Compress -Depth 5)"
        throw "Isolated SessionHost identity check failed (expected PID $($hostProcess.Id))."
    }

    $created = Send-HostRequest @{ Command = 'create'; ProtocolVersion = 2; SessionId = $sessionId; CommandLine = 'cmd.exe /k'; WorkingDirectory = $PSScriptRoot }
    $leaseId = $created.LeaseId
    if ($created.ProcessId -le 0 -or [string]::IsNullOrWhiteSpace($leaseId)) { throw 'No shell PID or lease returned.' }

    Send-HostRequest @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId; Data = "echo CMUX_M5_BEFORE`r" } | Out-Null
    Start-Sleep -Milliseconds 700
    $listed = Send-HostRequest @{ Command = 'list'; ProtocolVersion = 2 }
    $same = @($listed.Sessions | Where-Object SessionId -eq $sessionId)
    if ($same.Count -ne 1 -or $same[0].ProcessId -ne $created.ProcessId) { throw 'Session was lost after client disconnect.' }

    Start-Sleep -Seconds 2
    Send-HostRequest @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId; Data = "echo CMUX_M5_AFTER`r" } | Out-Null
    Start-Sleep -Milliseconds 700
    $attached = Send-HostRequest @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId }
    if ($attached.ProcessId -ne $created.ProcessId -or
        $attached.Replay -notmatch 'CMUX_M5_BEFORE' -or
        $attached.Replay -notmatch 'CMUX_M5_AFTER') {
        throw 'Reattached output missed a marker or shell PID changed.'
    }
    $exiting = Send-HostRequest @{ Command = 'create'; ProtocolVersion = 2; SessionId = $exitId; CommandLine = 'cmd.exe /c echo CMUX_NATURAL_EXIT'; WorkingDirectory = $PSScriptRoot }
    $exitLeaseId = $exiting.LeaseId
    Start-Sleep -Seconds 8
    $afterExit = Send-HostRequest @{ Command = 'list'; ProtocolVersion = 2 }
    Write-Host "Natural exit probe: sessions=$(@($afterExit.Sessions | Where-Object SessionId -eq $exitId).Count), shellAlive=$([bool](Get-Process -Id $exiting.ProcessId -ErrorAction SilentlyContinue))"
    if (@($afterExit.Sessions | Where-Object SessionId -eq $exitId).Count -ne 0 -or
        (Get-Process -Id $exiting.ProcessId -ErrorAction SilentlyContinue)) {
        throw 'Naturally exited shell was not cleaned up.'
    }
    Write-Host "M5/M10 isolated SessionHost smoke PASS: host PID $($hostProcess.Id), detached replay, stable PID, natural exit cleaned up."
}
finally {
    try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | Out-Null } catch { }
    try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $exitId; LeaseId = $exitLeaseId } | Out-Null } catch { }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue }
    else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
