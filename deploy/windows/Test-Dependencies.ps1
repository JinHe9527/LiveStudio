param(
    [string]$Solution = (Join-Path $PSScriptRoot '..\..\LiveStudio.slnx')
)

$ErrorActionPreference = 'Stop'
$reportText = & dotnet list $Solution package --vulnerable --include-transitive --no-restore --format json --output-version 1
if ($LASTEXITCODE -ne 0) {
    throw '依赖漏洞检查未完成，禁止放行。'
}
$report = ($reportText -join [Environment]::NewLine) | ConvertFrom-Json
if ($report.version -ne 1 -or -not $report.projects) {
    throw '依赖漏洞检查未返回有效项目报告。'
}
if ($report.problems) {
    throw ('依赖漏洞服务报告错误：' + ($report.problems | ConvertTo-Json -Depth 10 -Compress))
}
$findings = @(
    foreach ($project in $report.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package.vulnerabilities) {
                    "$($package.id) $($package.resolvedVersion) ($($project.path))"
                }
            }
        }
    }
)
if ($findings.Count -gt 0) {
    throw ('发现存在已知漏洞的依赖：' + [Environment]::NewLine + ($findings -join [Environment]::NewLine))
}
Write-Output "依赖漏洞检查通过，共 $(@($report.projects).Count) 个项目。"
