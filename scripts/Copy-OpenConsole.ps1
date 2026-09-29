param([Parameter(Mandatory = $true)][string]$Destination)

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dependencyFile = Join-Path $root 'packaging\terminal-engine.json'
$dependency = Get-Content -LiteralPath $dependencyFile -Raw | ConvertFrom-Json
$packageVersion = [string]$dependency.openConsolePackage
$expectedHash = ([string]$dependency.openConsoleSha256).ToUpperInvariant()
$packages = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'WindowsApps') -Directory `
    -Filter "Microsoft.WindowsTerminal_${packageVersion}_x64__*" -ErrorAction Stop
$source = $packages | ForEach-Object { Join-Path $_.FullName 'OpenConsole.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $source) {
    throw "Pinned Windows Terminal package $packageVersion with OpenConsole.exe was not found."
}
$actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $expectedHash) {
    throw "OpenConsole hash mismatch for $source. Expected $expectedHash, got $actualHash."
}
Copy-Item -LiteralPath $source -Destination $Destination -Force
Write-Host "OpenConsole source: $source"
Write-Host "OpenConsole SHA256: $actualHash"
