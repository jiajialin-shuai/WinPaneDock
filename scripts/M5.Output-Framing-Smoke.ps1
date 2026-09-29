param(
    # Defaults to the freshly built host. Point it at the MSIX stage to assert that the
    # binary that actually ships carries the fix, not just the dev build tree.
    [string]$HostExe
)

$ErrorActionPreference = 'Stop'

# Frame-alignment gate for the attach output channel.
#
# Production failure this guards against (see docs/M5-SESSION-HOST.md):
# a large, sustained output stream plus a momentarily stalled consumer made the host's
# 5s write deadline fire. NamedPipeProtocol.WriteLineAsync used to abandon the write
# instead of cancelling it, so the abandoned pipe write collided with the writer's
# dispose-time flush ("The stream is currently in use by a previous operation on the
# stream."), the attach pipe died, and the GUI's read loop exited on a truncated frame
# ("Terminal output was invalid") and never reconnected.
#
# The invariant asserted here is end-to-end: every frame on the wire must parse as JSON,
# and the payload must arrive complete and in order. A desynchronised reader cannot
# satisfy both, so framing regressions fail this gate.
#
# The payload is generated in one burst by PowerShell rather than by a cmd `for` loop:
# cmd echoes the expanded command line for every iteration (so each marker would appear
# twice) and its line editor wraps at the 120-column ConPTY, which would split markers
# mid-line. Neither is a framing fault, and both would make the ordering assertion lie.

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
$hostExe = if ($HostExe) { [IO.Path]::GetFullPath($HostExe) }
           else { Join-Path $root 'src/Cmux.SessionHost/bin/Release/net8.0-windows/Cmux.SessionHost.exe' }
if (-not (Test-Path -LiteralPath $hostExe)) { throw "SessionHost not found: $hostExe" }
Add-Type -Path $core
$instanceId = "framing-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
$logDirectory = [Cmux.Core.DiagnosticLog]::DirectoryPath
$artifactDirectory = Join-Path $root 'artifacts'
$hostProcess = $null
$sessionId = [guid]::NewGuid().ToString('N')
$leaseId = $null
$pipe = $null
$reader = $null
$writer = $null

# ~336 KB, so the payload spans several of the host's 64 KB output batches. Every line is
# 26 columns and deliberately hostile to JSON framing: a quote, a closing brace and a
# backslash all appear inside each line.
$expectedLines = 12000
# Stall the consumer on the first N frames. This is the production trigger: the host's
# writes must queue behind a slow reader. Kept far below the 5s write deadline so a stall
# can never legitimately kill the connection.
$stallLimit = 20
$stallMs = 150
$markerPrefix = 'CHK'
$generator = Join-Path $artifactDirectory 'framing-generator.ps1'
$forbidden = @(
    'The stream is currently in use by a previous operation on the stream',
    'SessionHost request deadline exceeded'
)

function Send-HostRequest($request) {
    $requestPipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $requestReader = $null
    $requestWriter = $null
    try {
        $requestPipe.Connect(5000)
        $requestWriter = [IO.StreamWriter]::new($requestPipe)
        $requestWriter.AutoFlush = $true
        $requestReader = [IO.StreamReader]::new($requestPipe)
        $requestWriter.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
        $task = $requestReader.ReadLineAsync()
        if (-not $task.Wait(10000)) { throw 'SessionHost response timed out.' }
        $response = $task.Result | ConvertFrom-Json
        if (-not $response.Ok) { throw $response.Error }
        return $response
    }
    finally {
        try { if ($requestReader) { $requestReader.Dispose() } } catch { }
        try { if ($requestWriter) { $requestWriter.Dispose() } } catch { }
        try { $requestPipe.Dispose() } catch { }
    }
}

function Get-HostLogText($processId) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd')
    $files = @(Get-ChildItem -LiteralPath $logDirectory -Filter "session-host-$stamp-$processId*.jsonl" -ErrorAction SilentlyContinue)
    if (-not $files) { return '' }
    return ($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

try {
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
    @'
param([int]$Count = 4000)
$builder = [Text.StringBuilder]::new()
for ($i = 1; $i -le $Count; $i++) {
    [void]$builder.Append('CHK').Append($i).Append('"}\}\0123456789ABCDEF').Append("`r`n")
}
[Console]::Out.Write($builder.ToString())
# Stay alive after the burst so the gate can assert the shell survived the flood; the
# gate's own teardown ends the host well before this expires.
Start-Sleep -Seconds 120
'@ | Set-Content -LiteralPath $generator -Encoding utf8

    $hostProcess = Start-Process -FilePath $hostExe -WindowStyle Hidden -PassThru
    $identity = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try { $identity = Send-HostRequest @{ Command = 'identity'; ProtocolVersion = 2 }; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $identity -or $identity.Identity.ProcessId -ne $hostProcess.Id) { throw 'Isolated host identity check failed.' }

    $created = Send-HostRequest @{
        Command = 'create'; ProtocolVersion = 2; SessionId = $sessionId
        CommandLine = "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$generator`" -Count $expectedLines"
        WorkingDirectory = $PSScriptRoot
    }
    $leaseId = $created.LeaseId
    if ([string]::IsNullOrWhiteSpace($leaseId)) { throw 'Create did not return a lease.' }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(5000)
    $writer = [IO.StreamWriter]::new($pipe)
    $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($pipe)
    $writer.WriteLine((@{ Command = 'attach'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | ConvertTo-Json -Compress))
    $attachTask = $reader.ReadLineAsync()
    if (-not $attachTask.Wait(15000)) { throw 'Attach response timed out.' }
    $attached = $attachTask.Result | ConvertFrom-Json
    if (-not $attached.Ok) { throw $attached.Error }

    $output = [Text.StringBuilder]::new()
    $frames = 0
    $malformed = 0
    $stalls = 0
    $pattern = "$markerPrefix(\d+)`""
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $lineTask = $reader.ReadLineAsync()
        if (-not $lineTask.Wait(10000)) { break }
        $line = $lineTask.Result
        if ($null -eq $line) { break }
        try { $event = $line | ConvertFrom-Json }
        catch { $malformed++; continue }
        if ($event.Ok -and $event.Event -eq 'output' -and $event.Output) {
            [void]$output.Append($event.Output)
            $frames++
        }
        # Stall the consumer on purpose so the host's writes queue behind a slow reader.
        if ($stalls -lt $stallLimit) { $stalls++; Start-Sleep -Milliseconds $stallMs }
        if ([regex]::Matches($output.ToString(), $pattern).Count -ge $expectedLines) { break }
    }

    if ($malformed -ne 0) { throw "$malformed frame(s) on the attach stream were not valid JSON; the reader lost frame alignment." }
    if ($frames -eq 0) { throw 'No output frames arrived.' }
    # Guard against a vacuous pass: the gate is only meaningful if the consumer actually
    # stalled while the host was pushing, and the payload actually spanned several frames.
    if ($stalls -eq 0) { throw 'The consumer never stalled, so write backpressure was not exercised.' }
    if ($frames -lt 2) { throw "The payload arrived in a single frame ($frames); framing across frame boundaries was not exercised." }

    $indices = @([regex]::Matches($output.ToString(), $pattern) | ForEach-Object { [int]$_.Groups[1].Value })
    if ($indices.Count -ne $expectedLines) {
        throw "Expected $expectedLines markers, received $($indices.Count); output was lost or duplicated."
    }
    for ($i = 0; $i -lt $expectedLines; $i++) {
        if ($indices[$i] -ne ($i + 1)) {
            throw "Marker $($indices[$i]) arrived at position $i, expected $($i + 1); the output stream is misaligned."
        }
    }

    # Close the attach pipe, then let the host finish tearing the client down so the host
    # log holds the full record before it is scanned.
    try { $reader.Dispose() } catch { }
    try { $writer.Dispose() } catch { }
    try { $pipe.Dispose() } catch { }
    $reader = $null; $writer = $null; $pipe = $null
    Start-Sleep -Seconds 2

    $hostLog = Get-HostLogText $hostProcess.Id
    foreach ($needle in $forbidden) {
        if ($hostLog -and $hostLog.Contains($needle)) {
            throw "Host log records '$needle'. A write deadline abandoned an in-flight pipe write instead of cancelling it."
        }
    }
    if ($hostLog -match '"eventName":"host\.request\.timeout"[^}]*command=attach') {
        throw 'Host log records an attach write deadline; the consumer could not keep up.'
    }

    $shellAlive = [bool](Get-Process -Id $created.ProcessId -ErrorAction SilentlyContinue)
    if (-not $shellAlive) { throw 'The framing gate terminated the shell.' }
    Write-Host "Output framing PASS: isolated host $($hostProcess.Id), shell $($created.ProcessId), $frames frame(s), $expectedLines ordered markers, $stalls deliberate stalls, no malformed frames, no abandoned writes."
}
finally {
    try { if ($reader) { $reader.Dispose() } } catch { }
    try { if ($writer) { $writer.Dispose() } } catch { }
    try { if ($pipe) { $pipe.Dispose() } } catch { }
    try { Send-HostRequest @{ Command = 'close'; ProtocolVersion = 2; SessionId = $sessionId; LeaseId = $leaseId } | Out-Null } catch { }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $generator -Force -ErrorAction SilentlyContinue
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
