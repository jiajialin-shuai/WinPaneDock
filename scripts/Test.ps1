param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$IncludeDesktop
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Check([string]$Name, [scriptblock]$Action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $watch.Stop()
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'PASS'; Milliseconds = $watch.ElapsedMilliseconds })
        Write-Host "PASS  $Name ($($watch.ElapsedMilliseconds) ms)" -ForegroundColor Green
    }
    catch {
        $watch.Stop()
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'FAIL'; Milliseconds = $watch.ElapsedMilliseconds; Error = $_.Exception.Message })
        Write-Host "FAIL  $Name ($($watch.ElapsedMilliseconds) ms): $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "WinPaneDock regression entry: configuration=$Configuration, utc=$([DateTimeOffset]::UtcNow.ToString('O'))"
Invoke-Check 'build WPF and dependencies' {
    dotnet build (Join-Path $root 'spikes/M0.Terminal.Wpf/M0.Terminal.Wpf.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}
Invoke-Check 'workspace and focus smoke' {
    & pwsh -NoProfile -File (Join-Path $root 'scripts/M2.Workspace-Smoke.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'M2 smoke failed' }
}
Invoke-Check 'layout persistence smoke' {
    & pwsh -NoProfile -File (Join-Path $root 'scripts/M3.Layout-Smoke.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'M3 layout smoke failed' }
}
Invoke-Check 'agent detection smoke' {
    & pwsh -NoProfile -File (Join-Path $root 'scripts/M4.AgentDetection-Smoke.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'M4 agent smoke failed' }
}
Invoke-Check 'Git context smoke' {
    & pwsh -NoProfile -File (Join-Path $root 'scripts/M7.Git-Smoke.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'M7 Git smoke failed' }
}
Invoke-Check 'replay filter smoke' {
    $assembly = Join-Path $root "spikes/M0.Terminal.Wpf/bin/$Configuration/net8.0-windows/Cmux.Spike.Terminal.dll"
    & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Replay-Filter-Smoke.ps1') -AssemblyPath $assembly
    if ($LASTEXITCODE -ne 0) { throw 'M5 replay smoke failed' }
}
if ($Configuration -eq 'Release') {
    Invoke-Check 'isolated SessionHost lifecycle' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.SessionHost-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 SessionHost smoke failed' }
    }
    Invoke-Check 'output backpressure' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Output-Backpressure-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 output backpressure smoke failed' }
    }
    Invoke-Check 'output frame alignment' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Output-Framing-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 framing smoke failed' }
    }
    Invoke-Check 'output frame reader' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Frame-Reader-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 frame reader smoke failed' }
    }
    Invoke-Check 'explicit close persistence' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Close-Persist-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 close persistence smoke failed' }
    }
    Invoke-Check 'IPC deadline' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Ipc-Deadline-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 IPC deadline smoke failed' }
    }
    Invoke-Check 'session lease fence' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Lease-Fence-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 lease fence smoke failed' }
    }
    Invoke-Check 'large input chunking' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.Input-Chunk-Smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'M5 input chunk smoke failed' }
    }
}
if ($IncludeDesktop) {
    Invoke-Check 'isolated WPF restore' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M3.App-Restore-Smoke.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'M3 WPF restore smoke failed' }
    }
    Invoke-Check 'isolated WPF reattach' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M5.App-Reattach-Smoke.ps1') -Crash -DetachedSeconds 5 -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'M5 WPF reattach smoke failed' }
    }
    Invoke-Check 'output stream reattach' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M10.Reattach-Smoke.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'M10 output reattach smoke failed' }
    }
    Invoke-Check 'startup output replay' {
        & pwsh -NoProfile -File (Join-Path $root 'scripts/M10.Replay-Smoke.ps1') -Configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'M10 startup replay smoke failed' }
    }
}

$report = Join-Path $root "artifacts/test-$stamp.json"
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report -Encoding utf8
$failed = @($results | Where-Object Status -eq 'FAIL')
Write-Host "Regression report: $report"
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) regression check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All requested regression checks passed.' -ForegroundColor Green
exit 0
