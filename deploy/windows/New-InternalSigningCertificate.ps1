param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [Parameter(Mandatory = $true)]
    [SecureString]$Password,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'private-signing-output')
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "输出目录已存在，为避免覆盖签名身份已停止: $OutputDirectory"
}

New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Publisher `
    -FriendlyName 'LiveStudio Internal MSIX Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -HashAlgorithm SHA256 `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5)

$pfxPath = Join-Path $OutputDirectory 'LiveStudio-signing.pfx'
$cerPath = Join-Path $OutputDirectory 'LiveStudio-signing.cer'
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $Password | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null

@"
Publisher=$($certificate.Subject)
Thumbprint=$($certificate.Thumbprint)
PfxPath=$pfxPath
CertificatePath=$cerPath
"@ | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'identity.txt')

Write-Output "内部签名身份已生成。PFX 必须离线保管，不得提交 Git。"
Write-Output "Publisher: $($certificate.Subject)"
Write-Output "Thumbprint: $($certificate.Thumbprint)"
