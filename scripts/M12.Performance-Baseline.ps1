param(
    [int]$DurationSeconds = 10,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($DurationSeconds -lt 3) { throw 'DurationSeconds must be at least 3.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputPath) { $OutputPath = Join-Path $root ("artifacts/m12-baseline-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss')) }

function Get-TargetProcesses {
    Get-Process -Name Cmux.Spike.Terminal,Cmux.SessionHost,OpenConsole -ErrorAction SilentlyContinue
}

function Get-IoSnapshot {
    $ids = @(Get-TargetProcesses | ForEach-Object Id)
    if ($ids.Count -eq 0) { return @() }
    Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -in $ids } |
        Select-Object ProcessId, Name, ReadTransferCount, WriteTransferCount, OtherTransferCount
}

$initialProcesses = @(Get-TargetProcesses)
$initialById = @{}
foreach ($process in $initialProcesses) { $initialById[$process.Id] = $process }
$initialIo = @(Get-IoSnapshot)
$samples = [System.Collections.Generic.List[object]]::new()
$previousCpu = @{}
foreach ($process in $initialProcesses) { $previousCpu[$process.Id] = $process.CPU }
$started = Get-Date
for ($sample = 0; $sample -lt $DurationSeconds; $sample++) {
    $now = Get-Date
    $rows = foreach ($process in Get-TargetProcesses) {
        $before = if ($previousCpu.ContainsKey($process.Id)) { $previousCpu[$process.Id] } else { $null }
        [pscustomobject]@{
            timestamp = $now.ToUniversalTime().ToString('O')
            id = $process.Id
            name = $process.ProcessName
            cpuSeconds = [math]::Round($process.CPU, 3)
            cpuDeltaSeconds = if ($null -ne $before) { [math]::Round($process.CPU - $before, 3) } else { $null }
            workingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 2)
            privateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
            handles = $process.Handles
            threads = $process.Threads.Count
            path = $process.Path
        }
    }
    foreach ($process in Get-TargetProcesses) { $previousCpu[$process.Id] = $process.CPU }
    $samples.Add([pscustomobject]@{ sample = $sample; processes = @($rows) })
    Start-Sleep -Seconds 1
}
$ended = Get-Date
$finalIo = @(Get-IoSnapshot)
$ioDelta = foreach ($before in $initialIo) {
    $after = $finalIo | Where-Object ProcessId -eq $before.ProcessId | Select-Object -First 1
    if ($after) {
        [pscustomobject]@{
            processId = $before.ProcessId
            name = $before.Name
            readBytes = [int64]$after.ReadTransferCount - [int64]$before.ReadTransferCount
            writeBytes = [int64]$after.WriteTransferCount - [int64]$before.WriteTransferCount
            otherBytes = [int64]$after.OtherTransferCount - [int64]$before.OtherTransferCount
        }
    }
}
$report = [ordered]@{
    startedUtc = $started.ToUniversalTime().ToString('O')
    endedUtc = $ended.ToUniversalTime().ToString('O')
    durationSeconds = [math]::Round(($ended - $started).TotalSeconds, 2)
    machine = [ordered]@{ os = [Environment]::OSVersion.VersionString; processorCount = [Environment]::ProcessorCount; dotnet = (dotnet --version) }
    processCountAtStart = $initialProcesses.Count
    samples = $samples
    ioDelta = @($ioDelta)
    note = 'Read-only process baseline; it does not open or modify terminal sessions.'
}
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Performance baseline: $OutputPath"
Write-Host "Sampled $($initialProcesses.Count) Cmux/OpenConsole process(es) for $($report.durationSeconds) seconds."
