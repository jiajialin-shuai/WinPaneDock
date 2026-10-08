param(
    [string] $Configuration = 'Release',
    [string] $OpenConsolePath
)

# Builds a redistributable, no-install WinPaneDock bundle.
#
# The MSIX route needs a code-signing certificate that other machines trust.
# This script skips packaging entirely: it publishes the same self-contained
# binaries the MSIX carries and lays them out next to each other, so the
# receiver unzips it and runs Cmux.Spike.Terminal.exe. Nothing is registered
# with Windows, no administrator rights are needed, and OpenConsole.exe ships
# in the bundle so Windows Terminal does not have to be installed.
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.CmuxAppVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid CmuxAppVersion: $version" }

$stage = Join-Path $root 'artifacts\share-stage'
$expected = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\share-stage'))
if (-not ([IO.Path]::GetFullPath($stage)).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFullPath($stage) -ne $expected) {
    throw 'Share stage path escaped the workspace.'
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

foreach ($project in @(
    'spikes\M0.Terminal.Wpf\M0.Terminal.Wpf.csproj',
    'src\Cmux.SessionHost\Cmux.SessionHost.csproj',
    'src\Cmux.Cli\Cmux.Cli.csproj')) {
    dotnet publish (Join-Path $root $project) `
        -c $Configuration -r win-x64 --self-contained true -o $stage `
        -p:PublishSingleFile=false -v:q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

# Copy-OpenConsole.ps1 runs as part of the GUI build; fall back to the explicit
# path so a portable bundle can still be produced on a machine where the
# resolver picked a different source.
$buildOut = Join-Path $root "spikes\M0.Terminal.Wpf\bin\$Configuration\net8.0-windows"
$stagedOpenConsole = Join-Path $stage 'OpenConsole.exe'
if (-not (Test-Path -LiteralPath $stagedOpenConsole)) {
    $resolved = $OpenConsolePath
    if (-not $resolved) { $resolved = $env:CMUX_OPENCONSOLE_PATH }
    if (-not $resolved) { $resolved = Join-Path $buildOut 'OpenConsole.exe' }
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw 'OpenConsole.exe was not staged. Run scripts/Get-OpenConsoleFromRelease.ps1 or set CMUX_OPENCONSOLE_PATH.'
    }
    Copy-Item -LiteralPath $resolved -Destination $stagedOpenConsole -Force
}

foreach ($notice in @('LICENSE', 'NOTICE')) {
    $noticePath = Join-Path $root $notice
    if (Test-Path -LiteralPath $noticePath) {
        Copy-Item -LiteralPath $noticePath -Destination (Join-Path $stage $notice) -Force
    }
}

Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' | Remove-Item -Force

$metadata = [System.Collections.Generic.List[string]]::new()
$metadata.Add("version=$version")
$metadata.Add("commit=$(& git -C $root rev-parse HEAD 2>$null)")
$metadata.Add("sdk=$(dotnet --version)")
$metadata.Add("runtime=win-x64 self-contained")
$metadata.Add("openConsoleSha256=$((Get-FileHash $stagedOpenConsole -Algorithm SHA256).Hash)")
$metadata.Add("builtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))")
Set-Content -LiteralPath (Join-Path $stage 'build-metadata.txt') `
    -Value ($metadata -join [Environment]::NewLine) -Encoding utf8

$readme = @"
WinPaneDock $version (portable, no install)
========================================

Requirements: Windows x64 10.0.19041.0 or later. PowerShell 7 (pwsh) is the
default shell; install it with `winget install Microsoft.PowerShell`, or pick a
different shell from the Profile menu on first launch. Windows Terminal does
NOT need to be installed; OpenConsole.exe is included.

How to run
----------
Unzip anywhere (a path with spaces is fine) and start WinPaneDock:

    Cmux.Spike.Terminal.exe

There is nothing to install and nothing to register. Delete the folder to
remove the app. Session data, layouts and logs live in %LOCALAPPDATA%\cmux\
and are NOT removed with the folder.

The `cmux` command line tool is cmux.exe in this same folder. It talks to the
running window over a named pipe, so the window must be open:

    .\cmux.exe list

Upgrading
---------
Close the window and every terminal you care about first: closing the window
only detaches, and the shells keep running in the background SessionHost.
Then replace this folder with the new one.
"@
Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $readme -Encoding utf8

$zip = Join-Path $root "artifacts\WinPaneDock-$version-x64-portable.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
Write-Host "Portable bundle: $zip" -ForegroundColor Green
Write-Host "Size: $size MB"
Write-Host "Build metadata: $($metadata -join [Environment]::NewLine)" -ForegroundColor DarkGray