$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\msix-stage'))
$expected = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\msix-stage'))
if ($stage -ne $expected -or -not $stage.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'MSIX stage path escaped the workspace.'
}
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

Copy-Item -LiteralPath (Join-Path $root 'spikes\M0.Terminal.Wpf\bin\Release\net8.0-windows\OpenConsole.exe') `
    -Destination (Join-Path $stage 'OpenConsole.exe') -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Destination (Join-Path $stage 'AppxManifest.xml')
Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb' | Remove-Item -Force

$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Copy-Item -Path (Join-Path $root 'packaging\Assets\*.png') -Destination $assets

$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -File |
    Where-Object { $_.DirectoryName -match '\\x64$' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'Windows SDK makeappx.exe was not found.' }
$package = Join-Path $root 'artifacts\cmux-0.1.6.0-x64-unsigned.msix'
& $makeappx.FullName pack /d $stage /p $package /o | Select-Object -Last 8
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx pack failed.' }
Write-Host "MSIX package: $package"
