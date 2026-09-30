param(
    [Parameter(Mandatory = $true)][string]$Destination,
    # Download a specific release instead of the one pinned in packaging/terminal-engine.json.
    [string]$Version,
    # Download even when a copy is already present. Off by default so a warm CI cache is
    # reused; the hash check below is what actually makes a reused copy trustworthy.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Fetches OpenConsole.exe from the official Windows Terminal release.
#
# Why this exists: Copy-OpenConsole.ps1 can only copy from a Windows Terminal package that
# is already installed. The GitHub windows-2022 runner has neither Windows Terminal nor
# winget, so the install step that used to run there could never succeed and took the whole
# gui job with it. The portable x64 zip on the release page contains the same OpenConsole.exe
# the msix installs, so extracting it gives a usable terminal host with no package manager,
# no administrator rights, and no AppX registration.
#
# The version and the expected hash both come from packaging/terminal-engine.json, which is
# the single source of truth for the engine. The hash is verified and a mismatch is fatal:
# the release asset is fetched over the network, so "it downloaded" is not evidence that it
# is the binary the project pinned.

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dependencyFile = Join-Path $root 'packaging\terminal-engine.json'
$dependency = Get-Content -LiteralPath $dependencyFile -Raw | ConvertFrom-Json

$pinnedVersion = [string]$dependency.openConsolePackage
$expectedHash = ([string]$dependency.openConsoleSha256).ToUpperInvariant()
if ($Version) { $pinnedVersion = $Version }
if ($pinnedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid Windows Terminal version: $pinnedVersion" }

$staging = Join-Path ([IO.Path]::GetTempPath()) "cmux-openconsole-$pinnedVersion"
$archive = Join-Path $staging "Microsoft.WindowsTerminal_${pinnedVersion}_x64.zip"
New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $cached = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $Force -and $cached -eq $expectedHash) {
            Write-Host "OpenConsole.exe already at $Destination and matches the pinned build."
            return
        }
    }

    $url = "https://github.com/microsoft/terminal/releases/download/v$pinnedVersion/Microsoft.WindowsTerminal_${pinnedVersion}_x64.zip"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Write-Host "Downloading Windows Terminal $pinnedVersion (x64, portable)..."
        $ProgressPreference = 'SilentlyContinue'
        # The asset URL redirects to a signed release-assets host, so redirects must be
        # followed; Invoke-WebRequest does that by default on PowerShell 7.
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing -TimeoutSec 300
    }

    # The zip holds the binaries under a versioned root folder, so the entry is matched by
    # name rather than by path: Microsoft has changed that root folder between releases.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'OpenConsole.exe' } | Select-Object -First 1
        if (-not $entry) {
            $names = ($zip.Entries | ForEach-Object { $_.FullName }) -join ', '
            throw "OpenConsole.exe was not in $url. The archive held: $names"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    finally { $zip.Dispose() }

    $actualHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        throw @"
OpenConsole.exe from Windows Terminal $pinnedVersion does not match the pinned build.
  expected: $expectedHash
  actual:   $actualHash

The download is discarded. Check packaging/terminal-engine.json against the
release at https://github.com/microsoft/terminal/releases/tag/v$pinnedVersion.
"@
    }

    Write-Host "OpenConsole source: $url (official release zip)"
    Write-Host "OpenConsole SHA256: $actualHash (matches the pinned build)"
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
