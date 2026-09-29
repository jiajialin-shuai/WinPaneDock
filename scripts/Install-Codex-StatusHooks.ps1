$ErrorActionPreference = 'Stop'

$codexDirectory = Join-Path $env:USERPROFILE '.codex'
$hooksPath = Join-Path $codexDirectory 'hooks.json'
$cmuxDirectory = Join-Path $env:LOCALAPPDATA 'cmux'
$hookPath = Join-Path $cmuxDirectory 'Codex-StatusHook.ps1'

New-Item -ItemType Directory -Force -Path $codexDirectory, $cmuxDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Cmux-Codex-StatusHook.ps1') -Destination $hookPath -Force

$configuration = if (Test-Path -LiteralPath $hooksPath) {
    Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    [ordered]@{}
}
if (-not $configuration.Contains('hooks')) { $configuration['hooks'] = [ordered]@{} }

foreach ($event in @(
    @{ Name = 'UserPromptSubmit'; Status = 'working' },
    @{ Name = 'Stop'; Status = 'waiting' },
    @{ Name = 'SessionEnd'; Status = 'completed' }
)) {
    $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $hookPath + '" ' + $event.Status
    $existing = @($configuration['hooks'][$event.Name]) | Where-Object { $null -ne $_ }
    if ($existing | Where-Object { $_.hooks.command -contains $command }) { continue }
    $configuration['hooks'][$event.Name] = @($existing) + @{
        hooks = @(@{ type = 'command'; command = $command; timeout = 3 })
    }
}

if (Test-Path -LiteralPath $hooksPath) {
    Copy-Item -LiteralPath $hooksPath -Destination "$hooksPath.bak" -Force
}
$configuration | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $hooksPath -Encoding utf8
Write-Output "Codex status hooks installed: $hooksPath"
Write-Output 'In a new Codex CLI session, open /hooks and trust the three cmux status hooks.'
