param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

# Drives the output-stream recovery path in a real window. A dropped attach pipe used to
# kill the read loop permanently, freezing the pane for the rest of the GUI's lifetime; the
# gate requires the pane to recover on the same shell instead.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = Join-Path $root "src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
$exe = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.Spike.Terminal.exe"
$log = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/m10-reattach-smoke.log"
Add-Type -Path (Resolve-Path $assembly)
$instanceId = "reattach-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[ Cmux.Core.InstanceScope ]::Configure($instanceId)
$logDirectory = [Cmux.Core.DiagnosticLog]::DirectoryPath
$app = $null
$hostProcessId = $null

function Get-HostLogText($processId) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd')
    $files = @(Get-ChildItem -LiteralPath $logDirectory -Filter "session-host-$stamp-$processId*.jsonl" -ErrorAction SilentlyContinue)
    if (-not $files) { return '' }
    return ($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
}

function Get-IsolatedHostId {
    # The GUI passes the instance through CMUX_INSTANCE_ID, not --instance-id, so the host's
    # command line does not name it. The identity handshake is the only reliable way to learn
    # which host this gate owns; matching on process name alone would kill the user's host.
    $pipeName = [Cmux.Core.InstanceScope]::Qualify(
        'cmux-session-host-' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.Replace('-', '_'))
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
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    $app = Start-Process -FilePath $exe -ArgumentList "--instance-id `"$instanceId`" --reattach-smoke" `
        -PassThru -WindowStyle Hidden
    # Learn the host PID while the gate is still running; after it exits the pipe is gone.
    $hostProcessId = $null
    for ($attempt = 0; $attempt -lt 100 -and -not $hostProcessId; $attempt++) {
        $hostProcessId = Get-IsolatedHostId
        if (-not $hostProcessId) { Start-Sleep -Milliseconds 200 }
    }
    $exited = $app.WaitForExit(120000)
    if (-not $exited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; throw 'Reattach gate timed out.' }
    if (-not (Test-Path -LiteralPath $log)) { throw "Gate produced no log; exit code $($app.ExitCode)." }
    $result = (Get-Content -LiteralPath $log -Raw).Trim()
    if ($app.ExitCode -ne 0) { throw "Reattach gate failed: $result" }
    if (-not $hostProcessId) { throw 'Could not identify the isolated SessionHost.' }

    $hostLog = Get-HostLogText $hostProcessId
    if ($hostLog -and $hostLog -match 'stream is currently in use') {
        throw 'Host log records a concurrent-write abort during recovery.'
    }
    # A saturated replay buffer used to make every reconnect a guaranteed write deadline.
    if ($hostLog -and $hostLog -match '"eventName":"host\.request\.timeout"[^}]*command=attach') {
        throw 'Host log records an attach write deadline; the reconnect replay was still unbounded.'
    }
    Write-Host "Output reattach PASS: $result; isolated host $hostProcessId, shell identity preserved, no concurrent-write abort."
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    # Only ever kill the host this gate proved it owns. A leaked host would keep the build
    # output directory locked and break every later build.
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
