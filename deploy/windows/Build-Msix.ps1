param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [Parameter(Mandatory = $true)]
    [string]$Publisher,

    [Parameter(Mandatory = $true)]
    [string]$CertificateThumbprint,

    [Parameter(Mandatory = $true)]
    [uri]$TimestampUrl,

    [string]$UpdateManifestUrl = '',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\output\windows')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$buildIdentifier = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) "LiveStudio-MSIX-$buildIdentifier"
$desktopPublish = Join-Path $stagingRoot 'desktop'
$agentPublish = Join-Path $stagingRoot 'agent'
$payload = Join-Path $stagingRoot 'payload'
$desktopPayload = Join-Path $payload 'Desktop'
$agentPayload = Join-Path $payload 'Agent'
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeAppx = Get-ChildItem -Path $sdkRoot -Filter makeappx.exe -Recurse |
    Where-Object FullName -Match '\\x64\\makeappx\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
$signTool = Get-ChildItem -Path $sdkRoot -Filter signtool.exe -Recurse |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $makeAppx -or -not $signTool) {
    throw '没有找到 Windows SDK 的 MakeAppx.exe 或 SignTool.exe。'
}

try {
    New-Item -ItemType Directory -Path $desktopPublish, $agentPublish, $payload, $desktopPayload, $agentPayload, $OutputDirectory -Force | Out-Null
    $runtimeIdentifier = "win-$Architecture"

    & dotnet publish (Join-Path $repositoryRoot 'src\LiveStudio.Desktop\LiveStudio.Desktop.csproj') `
        -c Release -r $runtimeIdentifier --self-contained true -o $desktopPublish `
        -p:Version=$Version `
        -p:LiveStudioUpdatePublisher=$Publisher `
        -p:LiveStudioUpdateCertificateThumbprint=$CertificateThumbprint `
        "-p:LiveStudioUpdateManifestUrl=$UpdateManifestUrl"
    if ($LASTEXITCODE -ne 0) { throw '桌面端发布失败。' }

    & dotnet publish (Join-Path $repositoryRoot 'src\LiveStudio.Agent\LiveStudio.Agent.csproj') `
        -c Release -r $runtimeIdentifier --self-contained true -o $agentPublish `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw 'Agent 发布失败。' }

    Copy-Item -Path (Join-Path $desktopPublish '*') -Destination $desktopPayload -Recurse -Force
    Copy-Item -Path (Join-Path $agentPublish '*') -Destination $agentPayload -Recurse -Force
    Copy-Item -Path (Join-Path $PSScriptRoot 'Assets') -Destination (Join-Path $payload 'Assets') -Recurse -Force

    [xml]$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw
    $manifest.Package.Identity.SetAttribute('Version', $Version)
    $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $Architecture)
    $manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
    $manifestPath = Join-Path $payload 'AppxManifest.xml'
    $xmlSettings = [Xml.XmlWriterSettings]::new()
    $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $xmlSettings.Indent = $true
    $xmlWriter = [Xml.XmlWriter]::Create($manifestPath, $xmlSettings)
    try {
        $manifest.Save($xmlWriter)
    }
    finally {
        $xmlWriter.Dispose()
    }

    $packagePath = Join-Path $OutputDirectory "LiveStudio-$Version-$Architecture.msix"
    & $makeAppx pack /d $payload /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw 'MSIX 打包失败。' }

    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl.AbsoluteUri /td SHA256 $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'MSIX 签名失败。' }

    & (Join-Path $PSScriptRoot 'Test-InternalMsixSignature.ps1') `
        -PackagePath $packagePath `
        -ExpectedCertificateThumbprint $CertificateThumbprint `
        -ReportPath (Join-Path $OutputDirectory 'LiveStudio-signature.txt')

    Write-Output $packagePath
}
finally {
    if ([IO.Directory]::Exists($stagingRoot)) {
        [IO.Directory]::Delete($stagingRoot, $true)
    }
}
