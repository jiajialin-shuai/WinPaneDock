param([Parameter(Mandatory = $true)][string]$Destination)

$packages = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'WindowsApps') -Directory -Filter 'Microsoft.WindowsTerminal_*_x64__8wekyb3d8bbwe' -ErrorAction Stop
$source = $packages |
    Sort-Object { [version]($_.Name -replace '^Microsoft.WindowsTerminal_([^_]+)_.*$', '$1') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'OpenConsole.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $source) { throw 'A local x64 Windows Terminal installation with OpenConsole.exe is required for the M0 spike.' }
Copy-Item -LiteralPath $source -Destination $Destination -Force
Write-Host "OpenConsole source: $source"
