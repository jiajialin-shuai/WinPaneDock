$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\msix-stage'))
$expected = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\msix-stage'))
if ($stage -ne $expected -or -not $stage.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MSIX stage path escaped the workspace.'
}

$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.CmuxAppVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid CmuxAppVersion: $version" }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

dotnet publish (Join-Path $root 'spikes\M0.Terminal.Wpf\M0.Terminal.Wpf.csproj') `
    -c Release -r win-x64 --self-contained true -o $stage -p:PublishSingleFile=false -v:q
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $root 'src\Cmux.SessionHost\Cmux.SessionHost.csproj') `
    -c Release -r win-x64 --self-contained true -o $stage -p:PublishSingleFile=false -v:q
if ($LASTEXITCODE -ne 0) { throw 'SessionHost publish failed.' }
dotnet publish (Join-Path $root 'src\Cmux.Cli\Cmux.Cli.csproj') `
    -c Release -r win-x64 --self-contained true -o $stage -p:PublishSingleFile=false -v:q
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

$openConsoleBuildDir = Join-Path $root 'spikes\M0.Terminal.Wpf\bin\Release\net8.0-windows'
Copy-Item -LiteralPath (Join-Path $openConsoleBuildDir 'OpenConsole.exe') `
    -Destination (Join-Path $stage 'OpenConsole.exe') -Force
$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw)
$manifest.Package.Identity.SetAttribute('Version', $version)
$manifest.Save($manifestPath)
Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' | Remove-Item -Force

$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Copy-Item -Path (Join-Path $root 'packaging\Assets\*.png') -Destination $assets
$openConsoleHash = (Get-FileHash (Join-Path $stage 'OpenConsole.exe') -Algorithm SHA256).Hash
$commit = (& git -C $root rev-parse HEAD 2>$null)
$metadata = [System.Collections.Generic.List[string]]::new()
$metadata.Add("version=$version")
$metadata.Add("commit=$commit")
$metadata.Add("sdk=$(dotnet --version)")
$metadata.Add("openConsoleSha256=$openConsoleHash")
# Record which OpenConsole build was actually used, including when it differs
# from the reference combination in packaging/terminal-engine.json. The sidecar
# is written next to the binary by scripts/Copy-OpenConsole.ps1 and is not a
# publish item, so read it from the build output rather than the stage.
$provenance = Join-Path $openConsoleBuildDir 'OpenConsole.exe.provenance.txt'
if (Test-Path -LiteralPath $provenance) {
    foreach ($line in Get-Content -LiteralPath $provenance) {
        if ($line.Trim()) { $metadata.Add($line.Trim()) }
    }
}
else {
    $metadata.Add('openConsoleProvenance=unavailable')
}
$metadata.Add("builtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))")
Set-Content -LiteralPath (Join-Path $stage 'build-metadata.txt') -Value ($metadata -join [Environment]::NewLine) -Encoding utf8

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -File |
    Where-Object { $_.DirectoryName -match '\\x64$' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'Windows SDK makeappx.exe was not found.' }
$package = Join-Path $root "artifacts\WinPaneDock-$version-x64-unsigned.msix"
& $makeappx.FullName pack /d $stage /p $package /o | Select-Object -Last 8
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx pack failed.' }
Write-Host "MSIX package: $package"
Write-Host "Build metadata: $($metadata -join [Environment]::NewLine)" -ForegroundColor DarkGray
