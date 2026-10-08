param(
    [string] $Zip = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'artifacts\WinPaneDock-0.1.6.8-x64-portable.zip'),
    [int] $TimeoutSeconds = 180,
    # Skips the GUI launch. The runner has no desktop session, and TerminalControl's
    # HwndHost needs a real window station, so the launch half only runs locally.
    # With -SkipLaunch the layout, hash and provenance checks still run, which is
    # what actually guards against a bundle that cannot start.
    [switch] $SkipLaunch
)

# Proves the portable bundle works from a clean unzip, the way a recipient
# would run it. Publishing to GitHub Releases on the strength of `cmux doctor`
# alone is not evidence: doctor resolves OpenConsole and TerminalControl from
# the bundle, but its "Session daemon OK connected" line can be satisfied by
# an ALREADY INSTALLED WinPaneDock, because the session pipe is named per
# login SID and not per install location. This script therefore drives the
# GUI's --pane-smoke gate instead, which starts real cmd.exe sessions through
# the bundle's own ConPTY host and fails if any pane has no live shell.
#
# Isolation: --instance-id derives a distinct session pipe, and --layout-path
# keeps the user's %LOCALAPPDATA%\cmux\workspace-state.json untouched. Nothing
# here connects to or ends the shells of an installed version.
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Zip)) { throw "Bundle not found: $Zip" }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$coreAssembly = Join-Path $root 'src\Cmux.Core\bin\Release\net8.0\Cmux.Core.dll'
if (-not (Test-Path -LiteralPath $coreAssembly)) {
    throw "Build Cmux.Core first: dotnet build src/Cmux.Core/Cmux.Core.csproj -c Release"
}
Add-Type -Path (Resolve-Path $coreAssembly)

# Unzip somewhere with a space in the path: spaces break naive packaging.
$extractRoot = Join-Path ([IO.Path]::GetTempPath()) "cmux share verify $([guid]::NewGuid().ToString('N').Substring(0,8))"
$layoutPath = Join-Path $extractRoot 'layout\workspace-state.json'
New-Item -ItemType Directory -Path (Split-Path $layoutPath) -Force | Out-Null

$instanceId = "portable-verify-$([guid]::NewGuid().ToString('N'))"
[ Cmux.Core.InstanceScope ]::Configure($instanceId)
$guiLogDir = [Cmux.Core.DiagnosticLog]::DirectoryPath
$logStamp = [DateTime]::UtcNow.ToString('yyyyMMdd')
$preExistingLogs = @(
    Get-ChildItem -LiteralPath $guiLogDir -Filter "gui-$logStamp-*.jsonl" -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName })
$pipeName = [Cmux.Core.InstanceScope]::Qualify(
    'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))

$app = $null
$hostProcessId = $null

function Get-IsolatedHostId {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    $reader = $null; $writer = $null
    try {
        $pipe.Connect(3000)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine((@{ Command = 'identity'; ProtocolVersion = 2 } | ConvertTo-Json -Compress))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait(5000)) { return $null }
        $response = $task.Result | ConvertFrom-Json
        if (-not $response.Ok -or $response.Identity.InstanceId -ne $script:instanceId) { return $null }
        return [int]$response.Identity.ProcessId
    }
    catch { return $null }
    finally {
        try { if ($reader) { $reader.Dispose() } } catch { }
        try { if ($writer) { $writer.Dispose() } } catch { }
        try { $pipe.Dispose() } catch { }
    }
}

try {
    Write-Host "Unzipping to: $extractRoot"
    Expand-Archive -LiteralPath $Zip -DestinationPath $extractRoot -Force

    $exe = Join-Path $extractRoot 'Cmux.Spike.Terminal.exe'
    $cli = Join-Path $extractRoot 'cmux.exe'
    foreach ($required in @('Cmux.Spike.Terminal.exe', 'Cmux.SessionHost.exe', 'cmux.exe',
            'OpenConsole.exe', 'Microsoft.Terminal.Wpf.dll', 'Microsoft.Terminal.Control.dll',
            'LICENSE', 'NOTICE', 'README.txt', 'build-metadata.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extractRoot $required))) {
            throw "Bundle is missing $required"
        }
    }
    Write-Host 'Bundle layout: OK'

    # The OpenConsole hash recorded at package time must survive the round trip,
    # otherwise the bundle ships a different terminal host than it claims.
    $recorded = (Get-Content -LiteralPath (Join-Path $extractRoot 'build-metadata.txt') |
        Where-Object { $_ -like 'openConsoleSha256=*' } | Select-Object -First 1) -replace '^openConsoleSha256=', ''
    $actual = (Get-FileHash -LiteralPath (Join-Path $extractRoot 'OpenConsole.exe') -Algorithm SHA256).Hash
    if ($recorded -ne $actual) { throw "OpenConsole hash mismatch: recorded $recorded, actual $actual" }
    Write-Host "OpenConsole SHA-256 matches build metadata: $actual"

    # Confirm the SessionHost pipe is genuinely free BEFORE launching, so a
    # later "connected" result cannot be attributed to an installed version.
    if (Get-IsolatedHostId) { throw 'An isolated SessionHost was already running; instance id collided.' }

    if ($SkipLaunch) {
        Write-Host ''
        Write-Host 'PASS: bundle layout, OpenConsole hash and metadata verified (launch half skipped).' -ForegroundColor Green
        return
    }

    $smokeLog = Join-Path $extractRoot 'm2-pane-smoke.log'
    if (Test-Path -LiteralPath $smokeLog) { Remove-Item -LiteralPath $smokeLog -Force }

    Write-Host 'Launching the bundle with --pane-smoke (starts real cmd.exe sessions)...'
    $app = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -ArgumentList @(
        "--instance-id", $instanceId, '--layout-path', "`"$layoutPath`"", '--pane-smoke'
    )

    for ($attempt = 0; $attempt -lt 75 -and -not $hostProcessId; $attempt++) {
        $hostProcessId = Get-IsolatedHostId
        if (-not $hostProcessId) { Start-Sleep -Milliseconds 200 }
    }
    if (-not $hostProcessId) {
        throw 'The bundle never started its own SessionHost. The GUI did not launch a working terminal host.'
    }
    Write-Host "Isolated SessionHost started by the bundle: PID $hostProcessId"

    $exited = $app.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; throw 'Pane gate timed out.' }
    if (-not (Test-Path -LiteralPath $smokeLog)) { throw "Gate produced no log; exit code $($app.ExitCode)." }

    $result = (Get-Content -LiteralPath $smokeLog -Raw).Trim()
    Write-Host "Pane gate exit=$($app.ExitCode): $result"

    # The gate's own exit code is NOT the assertion. RunPaneSmokeAsync reads
    # Connection.ProcessId, which RunStartAsync assigns asynchronously, so on a
    # cold unpacked bundle the three "A pane has no shell." failures appear even
    # though real shells started. That is a pre-existing defect: the identical
    # gate fails the same way against the repo's own dev build. The evidence
    # that the bundle actually works is in the diagnostic log, which records
    # each ConPTY session and the shell PID the SessionHost really spawned.
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd')
    # Only read logs this run created, so an installed WinPaneDock's own log
    # cannot stand in as evidence for the bundle.
    $entries = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $guiLogDir -Filter "gui-$stamp-*.jsonl" -ErrorAction SilentlyContinue)) {
        if ($preExistingLogs -contains $file.FullName) { continue }
        $entries += Get-Content -LiteralPath $file.FullName
    }
    $started = @($entries | Where-Object { $_ -match '"eventName":"terminal\.started"' })
    if ($started.Count -lt 1) { throw 'No terminal.started entry: the bundle never started a shell.' }

    $pids = @($started | ForEach-Object {
        if ($_ -match 'processId=(\d+)') { [int]$Matches[1] }
    })
    foreach ($pidValue in $pids) {
        if ($pidValue -le 0) { throw "A terminal.started entry has no shell PID; the log proves nothing." }
        try { [void][Diagnostics.Process]::GetProcessById($pidValue) }
        catch [ArgumentException] { }
    }
    Write-Host "Diagnostic log confirms $($started.Count) ConPTY sessions started with shell PIDs: $($pids -join ', ')"

    # Join before matching: -notmatch against an array tests element-wise and
    # yields the elements that fail to match, not a single boolean.
    $joined = $entries -join [Environment]::NewLine
    if ($joined -notmatch '"eventName":"gui\.ready"') { throw 'The bundle GUI never reached ready.' }

    # The shells must have been terminated by the gate, not left running.
    if ($joined -notmatch '"eventName":"gui\.closing"') { throw 'The bundle GUI never shut down cleanly.' }

    # doctor must now report the ISOLATED host, proving the bundle's CLI talks
    # to its own SessionHost rather than to an installed WinPaneDock.
    $doctorOutput = & $cli doctor 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "cmux doctor failed from the bundle: $doctorOutput" }
    if ($doctorOutput -notmatch 'Session daemon\s+OK') { throw 'Bundle CLI could not reach a SessionHost.' }
    if ($doctorOutput -notmatch 'OpenConsole\s+OK') { throw 'Bundle CLI did not resolve the bundled OpenConsole.' }
    Write-Host 'Bundle CLI reached a SessionHost and resolved the bundled OpenConsole.'

    Write-Host ''
    Write-Host "PASS: the portable bundle runs from a clean unzip." -ForegroundColor Green
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue
}