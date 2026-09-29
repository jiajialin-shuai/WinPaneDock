param(
    [Parameter(Mandatory = $true)][string]$Destination,
    # Explicit OpenConsole.exe. Overrides discovery. Also read from CMUX_OPENCONSOLE_PATH.
    [string]$OpenConsolePath,
    # Fail unless the discovered binary matches the reference hash in
    # packaging/terminal-engine.json. Use this for reproducible release builds.
    [switch]$RequireReferenceBuild
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dependencyFile = Join-Path $root 'packaging\terminal-engine.json'
$dependency = Get-Content -LiteralPath $dependencyFile -Raw | ConvertFrom-Json
$referencePackage = [string]$dependency.openConsolePackage
$referenceHash = ([string]$dependency.openConsoleSha256).ToUpperInvariant()
$cacheDirectory = Join-Path $root 'packaging\.openconsole'

# Terminal host discovery, most specific source first. The first hit wins.
# 1. An explicit path supplied by the caller or the environment.
# 2. A copy cached under packaging/.openconsole by an earlier run.
# 3. The newest Windows Terminal package installed for the current user.
function Find-InstalledOpenConsole {
    $candidates = @()

    $windowsApps = Join-Path $env:ProgramFiles 'WindowsApps'
    if (Test-Path -LiteralPath $windowsApps) {
        # WindowsApps can deny enumeration on hardened machines; that is not fatal.
        $candidates += Get-ChildItem -LiteralPath $windowsApps -Directory `
            -Filter 'Microsoft.WindowsTerminal_*_x64__*' -ErrorAction SilentlyContinue
    }

    # Get-AppxPackage is the reliable source of InstallLocation when enumeration works.
    try {
        $candidates += Get-AppxPackage -Name Microsoft.WindowsTerminal -ErrorAction Stop |
            Where-Object { $_.InstallLocation } |
            ForEach-Object { Get-Item -LiteralPath $_.InstallLocation -ErrorAction SilentlyContinue }
    }
    catch {
        Write-Verbose "Get-AppxPackage lookup failed: $($_.Exception.Message)"
    }

    $candidates | Sort-Object { $_.Name } -Descending | ForEach-Object {
        $exe = Join-Path $_.FullName 'OpenConsole.exe'
        if (Test-Path -LiteralPath $exe -PathType Leaf) { $exe }
    }
}

$source = $null
$origin = $null

if ($OpenConsolePath) { $source = $OpenConsolePath; $origin = 'explicit path' }
elseif ($env:CMUX_OPENCONSOLE_PATH) { $source = $env:CMUX_OPENCONSOLE_PATH; $origin = 'CMUX_OPENCONSOLE_PATH' }
else {
    $cached = Join-Path $cacheDirectory 'OpenConsole.exe'
    if (Test-Path -LiteralPath $cached -PathType Leaf) { $source = $cached; $origin = 'packaging/.openconsole cache' }
}

if (-not $source) {
    $source = Find-InstalledOpenConsole | Select-Object -First 1
    $origin = 'installed Windows Terminal package'
}

if (-not $source) {
    throw @"
OpenConsole.exe was not found.

This project hosts ConPTY sessions with OpenConsole.exe from the Microsoft
Windows Terminal distribution. Install Windows Terminal and build again, or
point at a copy you already have:

  winget install --id Microsoft.WindowsTerminal

  # or supply the binary directly
  pwsh -NoProfile -File scripts/Copy-OpenConsole.ps1 -Destination <path> -OpenConsolePath <OpenConsole.exe>

Any Windows Terminal version works. The reference build recorded in
packaging/terminal-engine.json is $($referencePackage).
"@
}

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "OpenConsole.exe was not readable at $source"
}

$actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToUpperInvariant()
$isReference = $actualHash -eq $referenceHash

if (-not $isReference) {
    $message = @"
OpenConsole SHA256 does not match the reference build.
  source:   $source
  expected: $referenceHash
  actual:   $actualHash

The reference build is Windows Terminal $($referencePackage). A different
version is expected to work, but the exact engine used is recorded in the
package build metadata so a release can be traced back to a binary.
Pass -RequireReferenceBuild to fail instead of warning.
"@
    if ($RequireReferenceBuild) { throw $message }
    Write-Warning $message
}

New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $Destination -Force

# Cache only what we discovered ourselves. An explicit path is the caller's
# binary and must never overwrite the discovered reference copy.
if ($origin -eq 'installed Windows Terminal package') {
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $cacheDirectory 'OpenConsole.exe') -Force
}

# Machine-readable provenance for the build metadata step.
@(
    "openConsoleSource=$source"
    "openConsoleOrigin=$origin"
    "openConsoleSha256=$actualHash"
    "openConsoleReferencePackage=$referencePackage"
    "openConsoleReferenceMatch=$isReference"
) | Set-Content -LiteralPath "$Destination.provenance.txt" -Encoding utf8

Write-Host "OpenConsole source: $source ($origin)"
Write-Host "OpenConsole SHA256: $actualHash"
if ($isReference) { Write-Host "OpenConsole matches the reference build." }
else { Write-Host "OpenConsole differs from the reference build; the hash above is recorded in the package." }
