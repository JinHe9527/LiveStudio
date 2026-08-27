param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [uri]$TimestampUrl,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\output\windows')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
$resolvedChecksumPath = [IO.Path]::GetFullPath($ChecksumPath)
$resolvedCertificatePath = [IO.Path]::GetFullPath($CertificatePath)
$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($requiredPath in @($resolvedPackagePath, $resolvedChecksumPath, $resolvedCertificatePath)) {
    if (-not [IO.File]::Exists($requiredPath)) {
        throw "一键安装器缺少输入文件：$requiredPath"
    }
}

$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$signTool = Get-ChildItem -Path $sdkRoot -Filter signtool.exe -Recurse |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signTool) {
    throw '没有找到 Windows SDK 的 SignTool.exe。'
}

$publishDirectory = Join-Path ([IO.Path]::GetTempPath()) "LiveStudio-Setup-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $publishDirectory, $resolvedOutputDirectory -Force | Out-Null
    & dotnet publish (Join-Path $repositoryRoot 'src\LiveStudio.Setup\LiveStudio.Setup.csproj') `
        -c Release -r win-x64 --self-contained true -o $publishDirectory `
        "-p:LiveStudioPackagePath=$resolvedPackagePath" `
        "-p:LiveStudioChecksumPath=$resolvedChecksumPath" `
        "-p:LiveStudioCertificatePath=$resolvedCertificatePath"
    if ($LASTEXITCODE -ne 0) {
        throw 'LiveStudio 一键安装器发布失败。'
    }

    $publishedSetup = Join-Path $publishDirectory 'LiveStudio.Setup.exe'
    if (-not [IO.File]::Exists($publishedSetup)) {
        throw '一键安装器发布结果缺少 LiveStudio.Setup.exe。'
    }

    $setupPath = Join-Path $resolvedOutputDirectory 'LiveStudio-Setup.exe'
    Copy-Item -LiteralPath $publishedSetup -Destination $setupPath -Force
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl.AbsoluteUri /td SHA256 $setupPath
    if ($LASTEXITCODE -ne 0) {
        throw 'LiveStudio 一键安装器签名失败。'
    }

    & (Join-Path $PSScriptRoot 'Test-InternalAuthenticodeSignature.ps1') `
        -SignedFilePath $setupPath `
        -ExpectedCertificateThumbprint $CertificateThumbprint `
        -ReportPath (Join-Path $resolvedOutputDirectory 'LiveStudio-Setup.signature.txt')
    $verificationProcess = Start-Process `
        -FilePath $setupPath `
        -ArgumentList '--verify-only' `
        -WindowStyle Hidden `
        -PassThru
    if (-not $verificationProcess.WaitForExit(60000)) {
        Stop-Process -Id $verificationProcess.Id -Force -ErrorAction SilentlyContinue
        throw 'LiveStudio 一键安装器内置资源复验超时。'
    }
    if ($verificationProcess.ExitCode -ne 0) {
        $installerLog = Join-Path $env:ProgramData 'LiveStudio\Installer\install.log'
        $details = if ([IO.File]::Exists($installerLog)) {
            Get-Content -LiteralPath $installerLog -Raw
        }
        throw "LiveStudio 一键安装器内置资源复验失败。$([Environment]::NewLine)$details"
    }

    Write-Output $setupPath
}
finally {
    if ([IO.Directory]::Exists($publishDirectory)) {
        [IO.Directory]::Delete($publishDirectory, $true)
    }
}
