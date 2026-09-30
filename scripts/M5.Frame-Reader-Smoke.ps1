param()

$ErrorActionPreference = 'Stop'

# Frame-reader alignment gate for Cmux.Core.NamedPipeLineReader.
#
# Production failure this guards against (see docs/M5-SESSION-HOST.md):
# the reader returned as soon as it saw a newline and discarded the rest of the buffer it
# had already read. A read fills the whole buffer at a time, so any read that spanned a
# frame boundary threw away the beginning of the next frame. The stream desynchronised,
# every frame after that started mid-payload, and the GUI could not parse them. Each
# unparsable frame forced a reconnect, and once the reconnect budget was spent the pane
# froze until the terminal was restarted.
#
# The existing output framing gate cannot catch this: it reads the attach stream with
# StreamReader.ReadLineAsync, which keeps its own buffer and is correct by construction.
# It proves the host writes well-formed frames, not that cmux reads them back correctly.
# This gate drives the reader cmux actually uses, so a regression in it fails here.
#
# The frames are written by a separate process, which is the real topology: SessionHost
# writes to a pipe this reader drains. A payload larger than the pipe buffer blocks the
# writer until the reader catches up, so the two cannot share a thread.

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$core = Join-Path $root 'src/Cmux.Core/bin/Release/net8.0/Cmux.Core.dll'
if (-not (Test-Path -LiteralPath $core)) { throw "Cmux.Core not found: $core" }
Add-Type -Path $core

$failures = [System.Collections.Generic.List[string]]::new()
$staging = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$producerScript = Join-Path $staging 'frame-reader-producer.ps1'

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { Write-Host "PASS  $Name" -ForegroundColor Green }
    else {
        Write-Host "FAIL  $Name $Detail" -ForegroundColor Red
        $failures.Add($Name)
    }
}

# Writes frames to the pipe, then closes it so the reader reaches end of stream.
@'
param([string]$PipeName, [string]$PayloadFile)
$out = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::Out)
$out.Connect(15000)
$bytes = [IO.File]::ReadAllBytes($PayloadFile)
$out.Write($bytes, 0, $bytes.Length)
$out.Flush()
$out.Dispose()
'@ | Set-Content -LiteralPath $producerScript -Encoding utf8

# Drains every frame out of a pipe fed by a separate writer process. Returns the frames
# recovered: a reader that loses the tail of a read drops the frames that followed, and one
# that mis-joins them returns garbage.
function Read-Frames([string]$Payload, [int]$MaxChars = 8388608) {
    # The name is generated here because NamedPipeServerStream.Name is empty until a
    # client has connected, and the writer needs the name to connect.
    $name = "cmux-reader-gate-$([guid]::NewGuid().ToString('N'))"
    $payloadFile = Join-Path $staging "frame-reader-payload-$name.txt"
    [IO.File]::WriteAllText($payloadFile, $Payload, [Text.UTF8Encoding]::new($false))
    $server = $null; $reader = $null; $producer = $null
    try {
        $server = [IO.Pipes.NamedPipeServerStream]::new(
            $name, [IO.Pipes.PipeDirection]::In, 1,
            [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        # The writer has to be launched before waiting on the connection: the gate blocks
        # here, and it is the writer that connects.
        $producer = Start-Process -FilePath 'pwsh' -ArgumentList @(
            '-NoProfile', '-File', $producerScript, '-PipeName', $name, '-PayloadFile', $payloadFile) -PassThru -WindowStyle Hidden
        $server.WaitForConnection()
        $reader = [IO.StreamReader]::new($server, $true)

        $frames = [Cmux.Core.NamedPipeLineReader]::new($reader, $MaxChars)
        $read = [System.Collections.Generic.List[string]]::new()
        while ($true) {
            $line = $frames.ReadLineAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            $read.Add($line)
            if ($read.Count -gt 200000) { throw 'The reader returned frames indefinitely; it never reached end of stream.' }
        }
        return $read.ToArray()
    }
    finally {
        if ($producer -and -not $producer.HasExited) { try { $producer.Kill() } catch { } }
        try { $reader.Dispose() } catch { }
        try { $server.Dispose() } catch { }
        Remove-Item -LiteralPath $payloadFile -Force -ErrorAction SilentlyContinue
    }
}

function Join-Frames($Frames) { return ($Frames -join '|') }

try {
    # The production trigger: one read spans a frame boundary, so the second frame lives in
    # the buffer the first read already consumed. The reader dropped it.
    $two = Read-Frames "first`nsecond`n"
    Check 'two frames in one read' ((Join-Frames $two) -ceq 'first|second') "got '$(Join-Frames $two)'"

    $three = Read-Frames "a`nb`nc`n"
    Check 'three frames in one read' ((Join-Frames $three) -ceq 'a|b|c') "got '$(Join-Frames $three)'"

    # A trailing frame with no terminator is end-of-stream residue, and must still be returned.
    $tail = Read-Frames "a`nb"
    Check 'unterminated trailing frame' ((Join-Frames $tail) -ceq 'a|b') "got '$(Join-Frames $tail)'"

    # A frame larger than the reader's own chunk size cannot come back from a single read,
    # so the carry has to accumulate across several of them.
    $long = 'X' * 10000
    $longResult = Read-Frames "$long`ntail`n"
    Check 'frame larger than one read chunk' (($longResult.Count -eq 2) -and ($longResult[0] -ceq $long) -and ($longResult[1] -ceq 'tail')) `
        "got $($longResult.Count) frame(s)"

    # CRLF terminators must not leak a carriage return into the frame.
    $crlf = Read-Frames "a`r`n`r`nb`r`n"
    Check 'CRLF terminators' ((Join-Frames $crlf) -ceq 'a||b') "got '$(Join-Frames $crlf)'"

    # A frame carrying the characters that would break a naive line split.
    $hostile = 'CHK1"}' + [char]92 + '{0123'
    $hostileResult = Read-Frames ($hostile + "`n")
    Check 'quotes, brace and backslash inside a frame' ((Join-Frames $hostileResult) -ceq $hostile) "got '$(Join-Frames $hostileResult)'"

    $empty = Read-Frames ''
    Check 'empty stream returns no frame' ($empty.Count -eq 0) "got $($empty.Count) frame(s)"

    # An oversize frame must be reported once and then stepped over. A reader that stays
    # on it can only ever fail again, so the channel would be lost rather than one frame.
    $overName = "cmux-reader-gate-$([guid]::NewGuid().ToString('N'))"
    $overFile = Join-Path $staging "frame-reader-oversize-$overName.txt"
    [IO.File]::WriteAllText($overFile, ('Y' * 200) + "`nafter`n", [Text.UTF8Encoding]::new($false))
    $overPipe = $null; $overReader = $null; $overProducer = $null
    try {
        $overPipe = [IO.Pipes.NamedPipeServerStream]::new(
            $overName, [IO.Pipes.PipeDirection]::In, 1,
            [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous)
        $overProducer = Start-Process -FilePath 'pwsh' -ArgumentList @(
            '-NoProfile', '-File', $producerScript, '-PipeName', $overName, '-PayloadFile', $overFile) -PassThru -WindowStyle Hidden
        $overPipe.WaitForConnection()
        $overReader = [IO.StreamReader]::new($overPipe, $true)

        $overFrames = [Cmux.Core.NamedPipeLineReader]::new($overReader, 10)
        $overThrew = $false
        try { $null = $overFrames.ReadLineAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch [IO.InvalidDataException] { $overThrew = $true }
        Check 'oversize frame is reported' $overThrew
        $recovered = $null
        try { $recovered = $overFrames.ReadLineAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch [IO.InvalidDataException] { $recovered = '<threw again>' }
        Check 'the frame after an oversize frame is readable' ($recovered -ceq 'after') "got '$recovered'"
    }
    finally {
        if ($overProducer -and -not $overProducer.HasExited) { try { $overProducer.Kill() } catch { } }
        try { $overReader.Dispose() } catch { }
        try { $overPipe.Dispose() } catch { }
        Remove-Item -LiteralPath $overFile -Force -ErrorAction SilentlyContinue
    }

    # The attach response and the first output frames arrive in one stream, and in practice
    # in one read. The reader that parsed the response is handed to the output pump, which
    # continues with the same reader: the frames already sitting in its carry are what the
    # attach swallowed, and a stage that built a fresh reader would drop or mis-frame them.
    $handoffFrames = @(
        '{"ok":true,"sessionId":"s1","identity":null}',
        '{"ok":true,"event":"output","output":"first"}',
        '{"ok":true,"event":"output","output":"second"}',
        '{"ok":true,"event":"output","output":"third"}'
    )
    $handoffStream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes(($handoffFrames -join "`n") + "`n"))
    $handoffReader = [Cmux.Core.NamedPipeLineReader]::new([IO.StreamReader]::new($handoffStream), 1024)
    $attach = $handoffReader.ReadLineAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Check 'attach response is read first' ($attach -ceq $handoffFrames[0]) "got '$attach'"
    $recoveredOutput = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $line = $handoffReader.ReadLineAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        $recoveredOutput.Add($line)
    }
    Check 'output frames carried across the attach handoff' `
        (($recoveredOutput -join '|') -ceq ($handoffFrames[1..3] -join '|')) "got '$($recoveredOutput -join '|')'"

    # A burst far larger than the pipe buffer, so the reader has to grow and compact its
    # carry many times. Every frame must survive, in order: a lost or duplicated frame here
    # is exactly the corruption the output channel cannot recover from.
    $burst = [Text.StringBuilder]::new()
    $burstFrames = 4000
    for ($i = 0; $i -lt $burstFrames; $i++) {
        [void]$burst.Append('{"ok":true,"event":"output","output":"frame').Append($i).Append('"}').Append("`n")
    }
    $burstResult = Read-Frames $burst.ToString()
    $intact = $burstResult.Count -eq $burstFrames
    if ($intact) {
        for ($i = 0; $i -lt $burstFrames; $i++) {
            $expected = '{"ok":true,"event":"output","output":"frame' + $i + '"}'
            if ($burstResult[$i] -cne $expected) { $intact = $false; break }
        }
    }
    Check "$burstFrames-frame burst arrives complete and in order" $intact "got $($burstResult.Count) frame(s)"

    if ($failures.Count -gt 0) {
        Write-Host "$($failures.Count) frame reader check(s) failed." -ForegroundColor Red
        exit 1
    }
    Write-Host "Frame reader PASS: the reader keeps every frame a read spans, and recovers past an oversize frame." -ForegroundColor Green
    exit 0
}
finally {
    Remove-Item -LiteralPath $producerScript -Force -ErrorAction SilentlyContinue
}
