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
$resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
$resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path $resolvedReportPath) -Force | Out-Null

$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$signTool = Get-ChildItem -Path $sdkRoot -Filter signtool.exe -Recurse |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signTool) {
    throw '没有找到 Windows SDK 的 SignTool.exe。'
}

$verificationOutput = @(& $signTool verify /pa /v $resolvedPackagePath 2>&1)
$verificationExitCode = $LASTEXITCODE
$verificationText = $verificationOutput -join [Environment]::NewLine
$verificationText | Set-Content -LiteralPath $resolvedReportPath -Encoding utf8

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackagePath
$actualThumbprint = if ($signature.SignerCertificate) {
    $signature.SignerCertificate.Thumbprint
}
if (-not $actualThumbprint -or
    ![string]::Equals(
        $actualThumbprint,
        $ExpectedCertificateThumbprint,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "MSIX 签名证书指纹不匹配：$actualThumbprint"
}

if ($verificationExitCode -eq 0 -and $signature.Status -eq 'Valid') {
    return
}

$isExpectedInternalTrustResult =
    $verificationExitCode -ne 0 -and
    $signature.Status -in @('Valid', 'UnknownError', 'NotTrusted') -and
    $verificationText.IndexOf(
        'A certificate chain processed, but terminated in a root',
        [StringComparison]::Ordinal) -ge 0 -and
    $verificationText.IndexOf(
        'certificate which is not trusted by the trust provider.',
        [StringComparison]::Ordinal) -ge 0 -and
    $verificationText.IndexOf('bad digest', [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
    $verificationText.IndexOf('not signed', [StringComparison]::OrdinalIgnoreCase) -lt 0
if (-not $isExpectedInternalTrustResult) {
    throw "MSIX 签名复验失败：$($signature.Status)"
}

Add-Content -LiteralPath $resolvedReportPath -Encoding utf8 -Value @"

LiveStudio internal distribution verification:
- The Authenticode payload and signer certificate were present.
- The signer thumbprint matched $ExpectedCertificateThumbprint.
- The only SignTool failure was the expected untrusted self-signed root on the disposable runner.
- Fixed computers trust this certificate through Trusted People before the first installation.
"@
