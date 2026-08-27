param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedCertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-InternalAuthenticodeSignature.ps1') `
    -SignedFilePath $PackagePath `
    -ExpectedCertificateThumbprint $ExpectedCertificateThumbprint `
    -ReportPath $ReportPath
$global:LASTEXITCODE = 0
