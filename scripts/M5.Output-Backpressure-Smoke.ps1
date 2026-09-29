$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$hostExe = Join-Path $root 'src/Cmux.SessionHost/bin/Release/net8.0-windows/Cmux.SessionHost.exe'
Add-Type -Path $core
$instanceId = "output-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$previousBudget = $env:CMUX_OUTPUT_BACKLOG_CHARS
$env:CMUX_INSTANCE_ID = $instanceId
$env:CMUX_OUTPUT_BACKLOG_CHARS = '65536'
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$hostProcess = $null
$sessionId = [guid]::NewGuid().ToString('N')
$leaseId = $null
$slowPipe = $null
$slowReader = $null
$slowWriter = $null

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
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait(5000)) { throw 'SessionHost response timed out.' }
        $response = $task.Result | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
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
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $identity -or $identity.Identity.ProcessId -ne $hostProcess.Id) { throw 'Isolated host identity check failed.' }

    $created = Send-HostRequest @{ Command = 'create'; ProtocolVersion = 2; SessionId = $sessionId; CommandLine = 'cmd.exe /k'; WorkingDirectory = $PSScriptRoot }
    $leaseId = $created.LeaseId
    if ([string]::IsNullOrWhiteSpace($leaseId)) { throw 'Create did not return a lease.' }
    $slowPipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $slowPipe.Connect(5000)
    $slowWriter = [IO.StreamWriter]::new($slowPipe)
    $slowWriter.AutoFlush = $true
    $slowReader = [IO.StreamReader]::new($slowPipe)
    $slowWriter.WriteLine((@{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | ConvertTo-Json -Compress))
    $attachTask = $slowReader.ReadLineAsync()
    if (-not $attachTask.Wait(5000)) { throw 'Attach response timed out.' }
    $attached = $attachTask.Result | ConvertFrom-Json
    if (-not $attached.Ok) { throw $attached.Error }
    if (([string]$attached.Replay).Length -gt 1000000) { throw "Replay exceeded its 1,000,000-character budget: $($attached.Replay.Length)." }
    Start-Sleep -Seconds 1
    Send-HostRequest @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId; Data = "echo CMUX_OUTPUT_MARKER`r" } | Out-Null
    $probe = $null
    for ($attempt = 0; $attempt -lt 10 -and -not $probe; $attempt++) {
        Start-Sleep -Milliseconds 500
        $candidate = Send-HostRequest @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId }
        if ($candidate.Replay -match 'CMUX_OUTPUT_MARKER') { $probe = $candidate }
    }
    if (-not $probe) { throw 'Input/output probe did not reach the shell.' }
    Send-HostRequest @{ Command = 'input'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId; Data = "for /L %i in (1,1,10000) do @echo 0123456789012345678901234567890123456789012345678901234567890123`r" } | Out-Null

    Start-Sleep -Seconds 2
    $disconnectSeen = $false
    $disconnectDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while ([DateTimeOffset]::UtcNow -lt $disconnectDeadline) {
        $late = $slowReader.ReadLineAsync()
        if (-not $late.Wait(1000)) { break }
        try { $line = $late.Result } catch { $disconnectSeen = $true; break }
        if ([string]::IsNullOrEmpty($line)) { $disconnectSeen = $true; break }
        $event = $line | ConvertFrom-Json
        if (-not $event.Ok -and $event.Error -match 'backlog budget') { $disconnectSeen = $true; break }
    }
    if (-not $disconnectSeen) { throw 'Slow output consumer was not disconnected after exceeding its budget.' }
    $fresh = Send-HostRequest @{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId }
    if ($fresh.ProcessId -ne $created.ProcessId) { throw 'Fresh attach did not preserve the shell PID.' }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $stillResponsive = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }
    $watch.Stop()
    if ($stillResponsive.Identity.ProcessId -ne $hostProcess.Id) { throw 'Host identity changed after output flood.' }
    $shellAlive = [bool](Get-Process -Id $created.ProcessId -ErrorAction SilentlyContinue)
    if (-not $shellAlive) { throw 'Output backpressure terminated the shell.' }
    Write-Host "Output backpressure PASS: isolated host $($hostProcess.Id), shell $($created.ProcessId), slow consumer disconnected, identity latency $($watch.ElapsedMilliseconds)ms."
}
finally {
    try { if ($slowReader) { $slowReader.Dispose() } } catch { }
    try { if ($slowWriter) { $slowWriter.Dispose() } } catch { }
    try { if ($slowPipe) { $slowPipe.Dispose() } } catch { }
    try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | Out-Null } catch { }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
    if ($null -eq $previousBudget) { Remove-Item Env:CMUX_OUTPUT_BACKLOG_CHARS -ErrorAction SilentlyContinue } else { $env:CMUX_OUTPUT_BACKLOG_CHARS = $previousBudget }
}
