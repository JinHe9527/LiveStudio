param(
    [Parameter(Mandatory = $true)]
    [string]$SnapshotPackage,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\LiveStudio.Agent\Adapters')
)

$ErrorActionPreference = 'Stop'
$expectedVersion = '12.8.1.454484231'
$expectedFingerprint = '8216f9eec3a699ad9095eb3ec7857fcb49668022f455d707c8b9bb5e060a8e7a'
$adapterId = 'webcast-mate-12.8.1.454484231-8216f9ee-v3'
$keyId = 'livestudio-adapter-2026-8216f9ee-v3'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SnapshotPackage))
try {
    $entry = $archive.GetEntry('parameters.json')
    if ($null -eq $entry) {
        throw '存档缺少 parameters.json'
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        $snapshot = $reader.ReadToEnd() | ConvertFrom-Json -Depth 100
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$application = @($snapshot.applications | Where-Object { $_.kind -eq 1 })
if ($application.Count -ne 1) {
    throw '存档必须且只能包含一份直播伴侣配置'
}

$application = $application[0]
if ($application.version -ne $expectedVersion -or $application.structureFingerprint -ne $expectedFingerprint) {
    throw "存档版本或结构指纹不匹配: $($application.version) / $($application.structureFingerprint)"
}

$storeByPath = [ordered]@{
    'WBStore\effectConfigStore.json' = 'effect-config'
    'WBStore\effectStore.json'       = 'effect-store'
    'WBStore\filterStore.json'       = 'filter-store'
    'WBStore\sourceStore.json'       = 'source-store'
}
$stores = foreach ($path in $storeByPath.Keys) {
    [ordered]@{
        id = $storeByPath[$path]
        kind = 0
        location = $path
        container = $null
        requiresApplicationStop = $true
    }
}

$coverageByPath = @{}
foreach ($field in $application.fieldCoverage) {
    $coverageByPath[$field.nativePath] = $field
}

$previousDefinitionPath = Join-Path $OutputDirectory 'webcast-mate-12.8.1.454484231-68ba3cc2-v2.adapter.json'
if (-not [IO.File]::Exists($previousDefinitionPath)) {
    throw "找不到上一版签名字段清单: $previousDefinitionPath"
}

$previousDefinition = [IO.File]::ReadAllText($previousDefinitionPath) | ConvertFrom-Json -Depth 100
$currentSourceDocument = @($application.nativeDocuments | Where-Object {
    $_.relativePath -eq 'WBStore\sourceStore.json'
})[0]
$currentCameraType = @($currentSourceDocument.values | Where-Object {
    $_.jsonPointer -match '^/sourceStore/sceneSource/[^/]+/data/[^/]+/type$' -and $_.value -eq 'camera'
})[0]
$currentCameraSegments = $currentCameraType.jsonPointer.Split('/')
$currentSceneId = $currentCameraSegments[3]
$currentSourceId = $currentCameraSegments[5]
$currentEffectId = @($currentSourceDocument.values | Where-Object {
    $_.jsonPointer -eq "/sourceStore/sceneSource/$currentSceneId/data/$currentSourceId/effectConfigId"
})[0].value
$previousCameraType = @($previousDefinition.fields | Where-Object {
    $_.storeId -eq 'source-store' -and $_.nativePath -match '^/sourceStore/sceneSource/[^/]+/data/[^/]+/type$'
})[0]
$previousCameraSegments = $previousCameraType.nativePath.Split('/')
$previousSceneId = $previousCameraSegments[3]
$previousSourceId = $previousCameraSegments[5]
$previousEffectId = @($previousDefinition.fields | Where-Object {
    $_.storeId -eq 'effect-config' -and $_.nativePath -match '^/effectConfigStore/configs/[^/]+/'
})[0].nativePath.Split('/')[3]
$previousFieldKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($field in $previousDefinition.fields) {
    $runtimePath = $field.nativePath.Replace($previousSceneId, $currentSceneId)
    $runtimePath = $runtimePath.Replace($previousSourceId, $currentSourceId)
    $runtimePath = $runtimePath.Replace($previousEffectId, [string]$currentEffectId)
    [void]$previousFieldKeys.Add("$($field.storeId)`0$runtimePath")
}

function Get-UnifiedKind([string]$pointer) {
    if ($pointer.EndsWith('/deviceId', [StringComparison]::Ordinal)) { return 0 }
    if ($pointer.EndsWith('/width', [StringComparison]::Ordinal)) { return 1 }
    if ($pointer.EndsWith('/height', [StringComparison]::Ordinal)) { return 2 }
    if ($pointer.EndsWith('/rate', [StringComparison]::Ordinal)) { return 3 }
    if ($pointer.EndsWith('/format', [StringComparison]::Ordinal)) { return 4 }
    if ($pointer.EndsWith('/colorSpace', [StringComparison]::Ordinal)) { return 5 }
    if ($pointer.EndsWith('/videoRange', [StringComparison]::Ordinal)) { return 6 }
    if ($pointer.Contains('/filterData/filterDataList/', [StringComparison]::Ordinal)) {
        if ($pointer.EndsWith('/name', [StringComparison]::Ordinal) -or
            $pointer.EndsWith('/type', [StringComparison]::Ordinal)) { return 7 }
        if ($pointer.EndsWith('/enable', [StringComparison]::Ordinal)) { return 8 }
        if ($pointer.EndsWith('/resourceFilePath', [StringComparison]::Ordinal)) { return 11 }
        return 10
    }

    return 12
}

function Get-ValueType([string]$kind) {
    switch ($kind) {
        'String' { return 'string' }
        'Number' { return 'number' }
        'True' { return 'bool' }
        'False' { return 'bool' }
        'Array' { return 'array' }
        'Object' { return 'object' }
        'Null' { return 'null' }
        default { throw "不支持的字段类型: $kind" }
    }
}

function Get-ControlKind([string]$kind, [string]$pointer) {
    if ($pointer.Contains('/curves/', [StringComparison]::Ordinal)) { return 'Curve' }
    switch ($kind) {
        'True' { return 'Toggle' }
        'False' { return 'Toggle' }
        'Number' { return 'Number' }
        'String' { return 'Text' }
        'Array' { return 'Collection' }
        'Object' { return 'Group' }
        default { return 'NativeValue' }
    }
}

$fields = [System.Collections.Generic.List[object]]::new()
$order = 0
foreach ($document in $application.nativeDocuments) {
    if (-not $storeByPath.Contains($document.relativePath)) {
        throw "存在未声明的配置存储: $($document.relativePath)"
    }

    $storeId = $storeByPath[$document.relativePath]
    foreach ($value in $document.values) {
        $coverageKey = "$($document.relativePath):$($value.jsonPointer)"
        if (-not $coverageByPath.ContainsKey($coverageKey)) {
            throw "字段缺少中文界面映射: $coverageKey"
        }

        $coverage = $coverageByPath[$coverageKey]
        $applicationManaged = $value.jsonPointer -eq '/effectStore/giftPlayConfig/alphaV2GiftIds' -or
            $value.jsonPointer -match '/filterData/filterDataList/\d+/id$' -or
            $value.jsonPointer -match '^/effectStore/deviceFacing/[^/]+$'
        $optionalVersionField = -not $previousFieldKeys.Contains("${storeId}`0$($value.jsonPointer)")
        $fields.Add([ordered]@{
            id = "${storeId}:$($value.jsonPointer)"
            unifiedKind = Get-UnifiedKind $value.jsonPointer
            storeId = $storeId
            nativePath = $value.jsonPointer
            valueType = Get-ValueType $coverage.valueType
            required = -not $applicationManaged -and -not $optionalVersionField
            writable = -not $applicationManaged
            nativeName = $coverage.nativeName
            uiPath = $coverage.uiPath
            order = $order
            controlKind = if ($applicationManaged) { 'ApplicationManaged' } else { Get-ControlKind $coverage.valueType $value.jsonPointer }
            defaultValueJson = $null
            minimum = $null
            maximum = $null
            step = $null
            options = $null
            internalIdPath = $null
            evidenceStatus = 2
        })
        $order++
    }
}

if ($fields.Count -ne 1042) {
    throw "字段总数不是预期的 1042，实际为 $($fields.Count)"
}

$requiredKinds = 0..6
foreach ($kind in $requiredKinds) {
    if (-not ($fields | Where-Object { $_.unifiedKind -eq $kind })) {
        throw "缺少必需视频字段类型: $kind"
    }
}

$liveStatePlaceholder = $fields | Where-Object {
    $_.storeId -eq 'effect-store' -and $_.valueType -eq 'bool'
} | Select-Object -First 1
if ($null -eq $liveStatePlaceholder) {
    throw '找不到适配定义格式要求的布尔字段'
}

$definition = [ordered]@{
    id = $adapterId
    minimumVersion = $expectedVersion
    maximumVersion = '12.9.2.470033184'
    structureFingerprint = $expectedFingerprint
    stores = @($stores)
    fields = @($fields)
    excludedNativePaths = @()
    liveStateRule = [ordered]@{
        storeId = $liveStatePlaceholder.storeId
        nativePath = $liveStatePlaceholder.nativePath
        expectedIdleValue = 'false'
    }
    screenshotRule = [ordered]@{ method = 'window'; target = 'main' }
    uiSections = @(
        [ordered]@{ id = 'basic'; nativeName = '基础设置'; uiPath = '基础设置'; order = 0; parentId = $null },
        [ordered]@{ id = 'beauty'; nativeName = '美颜设置'; uiPath = '美颜设置'; order = 1; parentId = $null },
        [ordered]@{ id = 'makeup'; nativeName = '美妆设置'; uiPath = '美妆设置'; order = 2; parentId = $null },
        [ordered]@{ id = 'filter'; nativeName = '滤镜设置'; uiPath = '滤镜设置'; order = 3; parentId = $null },
        [ordered]@{ id = 'effect'; nativeName = '特效道具'; uiPath = '特效道具'; order = 4; parentId = $null },
        [ordered]@{ id = 'chroma'; nativeName = '绿幕抠图'; uiPath = '绿幕抠图'; order = 5; parentId = $null },
        [ordered]@{ id = 'lens'; nativeName = '镜头特效'; uiPath = '镜头特效'; order = 6; parentId = $null },
        [ordered]@{ id = 'import'; nativeName = '导入导出'; uiPath = '导入导出'; order = 7; parentId = $null }
    )
    onlineCaptureStrategy = 'DoubleReadHash'
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
$definitionJson = $definition | ConvertTo-Json -Depth 100 -Compress
$definitionBytes = $utf8.GetBytes($definitionJson)
$definitionSha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($definitionBytes))
$key = [Security.Cryptography.ECDsa]::Create()
try {
    $key.GenerateKey([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $signature = [ordered]@{
        algorithm = 'ECDSA-P256-SHA256'
        keyId = $keyId
        definitionSha256 = $definitionSha256
        signatureBase64 = [Convert]::ToBase64String($key.SignData(
            $definitionBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256))
    }
    $publicKeyPem = $key.ExportSubjectPublicKeyInfoPem()
}
finally {
    if ($null -ne $key) {
        $key.Dispose()
    }
}

$trustedKeys = Join-Path $OutputDirectory 'trusted-keys'
[IO.Directory]::CreateDirectory($trustedKeys) | Out-Null
$definitionPath = Join-Path $OutputDirectory "$adapterId.adapter.json"
$signaturePath = Join-Path $OutputDirectory "$adapterId.signature.json"
$publicKeyPath = Join-Path $trustedKeys "$keyId.pem"
[IO.File]::WriteAllBytes($definitionPath, $definitionBytes)
[IO.File]::WriteAllText($signaturePath, ($signature | ConvertTo-Json -Compress), $utf8)
[IO.File]::WriteAllText($publicKeyPath, $publicKeyPem, $utf8)

Write-Output "适配定义: $definitionPath"
Write-Output "签名: $signaturePath"
Write-Output "受信任公钥: $publicKeyPath"
Write-Output "字段: $($fields.Count)"
Write-Output "SHA-256: $definitionSha256"
