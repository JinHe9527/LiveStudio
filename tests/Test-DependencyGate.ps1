$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot '..\deploy\windows\Test-Dependencies.ps1'
$cases = @(
    @{ Name = 'clean'; ExitCode = 0; Json = '{"version":1,"projects":[{"path":"test.csproj"}]}'; Pass = $true },
    @{ Name = 'vulnerable'; ExitCode = 0; Json = '{"version":1,"projects":[{"path":"test.csproj","frameworks":[{"transitivePackages":[{"id":"unsafe","resolvedVersion":"1.0","vulnerabilities":[{"severity":"High"}]}]}]}]}'; Pass = $false },
    @{ Name = 'service-error'; ExitCode = 0; Json = '{"version":1,"projects":[{"path":"test.csproj"}],"problems":[{"message":"offline"}]}'; Pass = $false },
    @{ Name = 'command-error'; ExitCode = 1; Json = '{}'; Pass = $false },
    @{ Name = 'invalid-json'; ExitCode = 0; Json = 'invalid'; Pass = $false },
    @{ Name = 'missing-projects'; ExitCode = 0; Json = '{"version":1,"projects":[]}'; Pass = $false }
)
function dotnet {
    $global:LASTEXITCODE = $global:LiveStudioDependencyGateTestCase.ExitCode
    Write-Output $global:LiveStudioDependencyGateTestCase.Json
}
foreach ($global:LiveStudioDependencyGateTestCase in $cases) {
    $passed = $false
    try {
        & $gate | Out-Null
        $passed = $true
    }
    catch {
        if ($global:LiveStudioDependencyGateTestCase.Pass) { throw }
    }
    if ($passed -ne $global:LiveStudioDependencyGateTestCase.Pass) {
        throw "依赖检查门测试失败：$($global:LiveStudioDependencyGateTestCase.Name)"
    }
}
$global:LASTEXITCODE = 0
Remove-Variable -Name LiveStudioDependencyGateTestCase -Scope Global
Write-Output "依赖检查门 $($cases.Count) 项测试通过。"
