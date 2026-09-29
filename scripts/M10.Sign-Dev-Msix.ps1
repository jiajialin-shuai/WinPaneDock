$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$props = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.CmuxAppVersion
$unsigned = Join-Path $root "artifacts\WinPaneDock-$version-x64-unsigned.msix"
$signed = Join-Path $root "artifacts\WinPaneDock-$version-x64-dev.msix"
$publicCert = Join-Path $root 'artifacts\cmux-dev.cer'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw)
$publisher = $manifest.Package.Identity.Publisher

if (-not (Test-Path -LiteralPath $unsigned)) { throw "Package not found: $unsigned" }
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.FriendlyName -eq 'cmux MSIX development' } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature `
        -Subject $publisher -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -FriendlyName 'cmux MSIX development'
}

$signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -File |
    Where-Object { $_.DirectoryName -match '\\x64$' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signTool) { throw 'Windows SDK signtool.exe was not found.' }

Copy-Item -LiteralPath $unsigned -Destination $signed -Force
& $signTool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /s My $signed
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
Export-Certificate -Cert $cert -FilePath $publicCert -Force | Out-Null
Write-Host "Development package: $signed"
Write-Host "Public certificate: $publicCert"
Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
