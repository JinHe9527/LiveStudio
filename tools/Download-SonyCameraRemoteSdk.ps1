param(
    [string]$DestinationDirectory = (Join-Path $env:USERPROFILE 'Downloads'),
    [int]$Connections = 12
)

$ErrorActionPreference = 'Stop'
$expectedLength = 213268163L
$fileName = 'CrSDK_v2.02.00_20260610a_Win64.zip'
$downloadUrl = 'https://di.update.sony.net/NEX/Uhfc28896b/CrSDK_v2.02.00_20260610a_Win64.zip'
$destinationRoot = [IO.Path]::GetFullPath($DestinationDirectory)
$initialPart = [IO.Path]::GetFullPath((Join-Path $destinationRoot "$fileName.partial"))
$chunkDirectory = [IO.Path]::GetFullPath((Join-Path $destinationRoot 'CrSDK_v2.02.00_chunks'))
$destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $fileName))

foreach ($path in @($initialPart, $chunkDirectory, $destination)) {
    if (-not $path.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "下载目标越界：$path"
    }
}

if (-not (Test-Path -LiteralPath $initialPart)) {
    throw "找不到初始下载分段：$initialPart"
}
if (Test-Path -LiteralPath $chunkDirectory) {
    throw "分段目录已存在：$chunkDirectory"
}
if (Test-Path -LiteralPath $destination) {
    throw "目标 SDK 已存在：$destination"
}

New-Item -ItemType Directory -Path $chunkDirectory | Out-Null
$initialLength = (Get-Item -LiteralPath $initialPart).Length
if ($initialLength -le 0 -or $initialLength -ge $expectedLength) {
    throw "初始分段长度无效：$initialLength"
}

$remainingLength = $expectedLength - $initialLength
$segmentLength = [long][Math]::Ceiling($remainingLength / [double]$Connections)
$segments = @()
for ($index = 0; $index -lt $Connections; $index++) {
    $start = $initialLength + [long]($index * $segmentLength)
    if ($start -ge $expectedLength) {
        break
    }

    $end = [Math]::Min($expectedLength - 1, $start + $segmentLength - 1)
    $path = Join-Path $chunkDirectory ("chunk-{0:D2}-{1}-{2}.part" -f $index, $start, $end)
    $arguments = @(
        '-sS', '-L', '--fail', '--retry', '5', '--retry-all-errors',
        '--connect-timeout', '30', '--range', "$start-$end", '--output', $path, $downloadUrl)
    $process = Start-Process -FilePath 'curl.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $segments += [pscustomobject]@{
        Index = $index
        Start = $start
        End = $end
        Path = $path
        Process = $process
    }
}

while (@($segments | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
    Start-Sleep -Seconds 15
    $downloaded = ($segments | ForEach-Object {
        if (Test-Path -LiteralPath $_.Path) {
            (Get-Item -LiteralPath $_.Path).Length
        } else {
            0
        }
    } | Measure-Object -Sum).Sum
    $active = @($segments | Where-Object { -not $_.Process.HasExited }).Count
    "SDK 分段下载：$([Math]::Round(($initialLength + $downloaded) / $expectedLength * 100, 1))%（$active 个连接）"
}

foreach ($segment in $segments) {
    if ($segment.Process.ExitCode -ne 0) {
        throw "分段 $($segment.Index) 下载失败，curl=$($segment.Process.ExitCode)"
    }

    $expectedSegmentLength = $segment.End - $segment.Start + 1
    $actualSegmentLength = (Get-Item -LiteralPath $segment.Path).Length
    if ($actualSegmentLength -ne $expectedSegmentLength) {
        throw "分段 $($segment.Index) 长度错误：$actualSegmentLength/$expectedSegmentLength"
    }
}

$assemblingPath = "$destination.assembling"
$output = [IO.File]::Open(
    $assemblingPath,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None)
try {
    $parts = @($initialPart) + @($segments | Sort-Object Index | ForEach-Object { $_.Path })
    foreach ($part in $parts) {
        $input = [IO.File]::OpenRead($part)
        try {
            $input.CopyTo($output)
        } finally {
            $input.Dispose()
        }
    }
    $output.Flush($true)
} finally {
    $output.Dispose()
}

$assembledLength = (Get-Item -LiteralPath $assemblingPath).Length
if ($assembledLength -ne $expectedLength) {
    throw "合并长度错误：$assembledLength/$expectedLength"
}

Move-Item -LiteralPath $assemblingPath -Destination $destination
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
[pscustomobject]@{
    Path = $destination
    Length = $assembledLength
    Sha256 = $hash
}
