param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

# Startup-output gate. TerminalContainer discards output that arrives before the HwndHost has
# built its native terminal, so a TUI that paints once and then goes quiet (grok, opencode)
# stays invisible until the user resizes something. This drives that exact ordering and
# requires the buffered tail to be replayed once the control is realised.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = Join-Path $root "src/Cmux.Core/bin/$Configuration/net8.0/Cmux.Core.dll"
$exe = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.Spike.Terminal.exe"
$log = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/m10-replay-smoke.log"
Add-Type -Path (Resolve-Path $assembly)
$guiLogDir = [Cmux.Core.DiagnosticLog]::DirectoryPath
$instanceId = "replay-smoke-$([guid]::NewGuid().ToString('N'))"
$previousInstance = $env:CMUX_INSTANCE_ID
$env:CMUX_INSTANCE_ID = $instanceId
[Cmux.Core.InstanceScope]::Configure($instanceId)
$app = $null
$hostProcessId = $null

function Get-IsolatedHostId {
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
    $app = Start-Process -FilePath $exe -ArgumentList "--instance-id `"$instanceId`" --replay-smoke" `
        -PassThru -WindowStyle Hidden
    $hostProcessId = $null
    for ($attempt = 0; $attempt -lt 100 -and -not $hostProcessId; $attempt++) {
        $hostProcessId = Get-IsolatedHostId
        if (-not $hostProcessId) { Start-Sleep -Milliseconds 200 }
    }
    $exited = $app.WaitForExit(120000)
    if (-not $exited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; throw 'Replay gate timed out.' }
    if (-not (Test-Path -LiteralPath $log)) { throw "Gate produced no log; exit code $($app.ExitCode)." }
    $result = (Get-Content -LiteralPath $log -Raw).Trim()
    if ($app.ExitCode -ne 0) { throw "Replay gate failed: $result" }

    # The gate is only meaningful if the replay actually fired.
    $guiFiles = @(Get-ChildItem -LiteralPath $guiLogDir -Filter "gui-$(Get-Date).ToString('yyyyMMdd')-*.jsonl" -ErrorAction SilentlyContinue)
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd')
    $replayed = $false
    foreach ($file in @(Get-ChildItem -LiteralPath $guiLogDir -Filter "gui-$stamp-*.jsonl" -ErrorAction SilentlyContinue)) {
        if ((Get-Content -LiteralPath $file.FullName -Raw) -match 'terminal\.output\.replayed') { $replayed = $true }
    }
    if (-not $replayed) { throw 'The gate passed but no terminal.output.replayed was logged; it proved nothing.' }
    Write-Host "Startup replay PASS: $result; isolated host $hostProcessId, replay confirmed in the diagnostic log."
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($hostProcessId) { Stop-Process -Id $hostProcessId -Force -ErrorAction SilentlyContinue }
    if ($null -eq $previousInstance) { Remove-Item Env:CMUX_INSTANCE_ID -ErrorAction SilentlyContinue } else { $env:CMUX_INSTANCE_ID = $previousInstance }
}
