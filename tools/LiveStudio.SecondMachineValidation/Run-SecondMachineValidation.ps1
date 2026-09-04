[CmdletBinding()]
param(
    [string]$PackagePath = '',
    [string]$InstallerPath = '',
    [string]$OutputDirectory = '',
    [switch]$DiagnoseCapture,
    [switch]$ExecuteFiveRestoreCycles,
    [ValidateRange(1, 5)]
    [int]$CycleCount = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedInstallerSha256 = 'E84FAD5E486FE11A540274634FF09CA8144120BED51A33F4B799F64C471CD451'
$expectedPackageSha256 = '76038651369B963B1F949FAEE0767FC3D88AF72ECF1A7B8063A3AACCADBC2BC7'
$expectedSignerFingerprint = '0FBED9794A588A1D6B597A608CB3DFC944A17E536A83AED18C5384064AECFFE0'
$maximumMessageLength = 32 * 1024 * 1024
$startedAt = [DateTimeOffset]::Now
$experimentId = 'cross-machine-restore-' + $startedAt.ToString('yyyyMMdd-HHmmss')
$machineAlias = if ([string]::IsNullOrWhiteSpace($env:COMPUTERNAME)) { 'unknown-windows-machine' } else { $env:COMPUTERNAME }
$outputPath = Join-Path $OutputDirectory ($experimentId + '.json')
$scriptExitCode = 0

if ($DiagnoseCapture -and $ExecuteFiveRestoreCycles) {
    throw 'DiagnoseCapture and ExecuteFiveRestoreCycles cannot be used together.'
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $json = $Value | ConvertTo-Json -Depth 30
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = New-Object System.IO.FileStream(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Read-Exactly {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][byte[]]$Buffer,
        [Parameter(Mandatory = $true)][int]$Count,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $offset = 0
    while ($offset -lt $Count) {
        $pending = $Stream.BeginRead($Buffer, $offset, $Count - $offset, $null, $null)
        try {
            if (-not $pending.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))) {
                throw "Timed out waiting for LiveStudio Agent after $TimeoutSeconds seconds."
            }

            $read = $Stream.EndRead($pending)
        }
        finally {
            $pending.AsyncWaitHandle.Dispose()
        }

        if ($read -le 0) {
            throw 'LiveStudio Agent closed the control pipe before returning a complete response.'
        }

        $offset += $read
    }
}

function Invoke-LiveStudioAgent {
    param(
        [Parameter(Mandatory = $true)][int]$Method,
        [Parameter(Mandatory = $true)]$Payload,
        [int]$TimeoutSeconds = 15
    )

    $requestId = [Guid]::NewGuid()
    $request = [ordered]@{
        requestId = $requestId
        method = $Method
        payload = $Payload
    }
    $requestBytes = [System.Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 20 -Compress))
    if ($requestBytes.Length -gt $maximumMessageLength) {
        throw 'LiveStudio Agent request exceeds the protocol size limit.'
    }

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.',
        'LiveStudio.Agent.Control',
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $header = [BitConverter]::GetBytes([int]$requestBytes.Length)
        $pipe.Write($header, 0, $header.Length)
        $pipe.Write($requestBytes, 0, $requestBytes.Length)
        $pipe.Flush()

        $responseHeader = New-Object byte[] 4
        Read-Exactly -Stream $pipe -Buffer $responseHeader -Count 4 -TimeoutSeconds $TimeoutSeconds
        $responseLength = [BitConverter]::ToInt32($responseHeader, 0)
        if ($responseLength -le 0 -or $responseLength -gt $maximumMessageLength) {
            throw "LiveStudio Agent returned an invalid response length: $responseLength."
        }

        $responseBytes = New-Object byte[] $responseLength
        Read-Exactly -Stream $pipe -Buffer $responseBytes -Count $responseLength -TimeoutSeconds $TimeoutSeconds
        $response = [System.Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json
        if ([string]$response.requestId -ne [string]$requestId) {
            throw 'LiveStudio Agent response ID does not match the request ID.'
        }

        if (-not $response.success) {
            $exception = New-Object System.InvalidOperationException(
                ([string]$response.errorCode + ': ' + [string]$response.errorMessage))
            $exception.Data['ErrorCode'] = [string]$response.errorCode
            throw $exception
        }

        return $response.result
    }
    finally {
        $pipe.Dispose()
    }
}

function Get-FileVersionFromCandidates {
    param(
        [string[]]$ProcessNames,
        [string[]]$FileCandidates,
        [string]$PathPattern = ''
    )

    foreach ($processName in $ProcessNames) {
        $processes = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
        foreach ($process in $processes) {
            try {
                if (-not [string]::IsNullOrWhiteSpace($process.Path) -and (Test-Path -LiteralPath $process.Path -PathType Leaf)) {
                    $item = Get-Item -LiteralPath $process.Path
                    return [ordered]@{
                        version = $item.VersionInfo.FileVersion
                        path = $item.FullName
                        running = $true
                    }
                }
            }
            catch {
                continue
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($PathPattern)) {
        foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
            try {
                if (-not [string]::IsNullOrWhiteSpace($process.Path) -and
                    $process.Path -match $PathPattern -and
                    (Test-Path -LiteralPath $process.Path -PathType Leaf)) {
                    $item = Get-Item -LiteralPath $process.Path
                    return [ordered]@{
                        version = $item.VersionInfo.FileVersion
                        path = $item.FullName
                        running = $true
                    }
                }
            }
            catch {
                continue
            }
        }
    }

    foreach ($candidate in $FileCandidates) {
        $matches = @(Get-ChildItem -Path $candidate -File -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
        foreach ($match in $matches) {
            return [ordered]@{
                version = $match.VersionInfo.FileVersion
                path = $match.FullName
                running = $false
            }
        }
    }

    return [ordered]@{
        version = ''
        path = ''
        running = $false
    }
}

function Get-CaptureDeviceEvidence {
    $pattern = '(?i)(magewell|pro capture|xi[0-9]+|4k[ -]?pro|capture|video ingest|video input|tianchuang|tchd)'
    try {
        $drivers = @(Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop)
    }
    catch {
        return @([ordered]@{
            name = ''
            manufacturer = ''
            hardwareId = ''
            driverVersion = ''
            driverProvider = ''
            physicalCandidate = $false
            error = $_.Exception.Message
        })
    }

    return @($drivers |
        Where-Object {
            ([string]$_.DeviceName -match $pattern) -or
            ([string]$_.Manufacturer -match $pattern) -or
            ([string]$_.DeviceID -match $pattern)
        } |
        Sort-Object DeviceName, DeviceID -Unique |
        ForEach-Object {
            $name = [string]$_.DeviceName
            [ordered]@{
                name = $name
                manufacturer = [string]$_.Manufacturer
                hardwareId = [string]$_.DeviceID
                driverVersion = [string]$_.DriverVersion
                driverProvider = [string]$_.DriverProviderName
                physicalCandidate = -not ($name -match '(?i)(virtual|obs|screen|desktop)')
                error = ''
            }
        })
}

function Convert-ApplicationStates {
    param($Applications)

    return @($Applications | ForEach-Object {
        [ordered]@{
            application = [int]$_.application
            adapterAvailable = [bool]$_.adapterAvailable
            running = [bool]$_.isRunning
            version = [string]$_.version
            status = [string]$_.statusMessage
        }
    })
}

function Get-LatestOperationForSnapshot {
    param(
        $State,
        [string]$SnapshotId
    )

    $operation = @($State.operations |
        Where-Object { [string]$_.snapshotId -eq $SnapshotId } |
        Sort-Object startedAt -Descending |
        Select-Object -First 1)
    if ($operation.Count -eq 0) {
        return $null
    }

    return [ordered]@{
        id = [string]$operation[0].id
        status = [int]$operation[0].status
        message = [string]$operation[0].message
        startedAt = [string]$operation[0].startedAt
        completedAt = [string]$operation[0].completedAt
    }
}

function Convert-RecentOperations {
    param($State)

    return @($State.operations |
        Sort-Object startedAt -Descending |
        Select-Object -First 20 |
        ForEach-Object {
            [ordered]@{
                id = [string]$_.id
                kind = [int]$_.kind
                status = [int]$_.status
                message = [string]$_.message
                snapshotId = [string]$_.snapshotId
                startedAt = [string]$_.startedAt
                completedAt = [string]$_.completedAt
            }
        })
}

function Resolve-UniqueMappings {
    param(
        [Parameter(Mandatory = $true)][string]$SnapshotId,
        [Parameter(Mandatory = $true)]$MappingContext
    )

    $decisions = @()
    $unmapped = @($MappingContext.sources | Where-Object { $null -eq $_.mapping })
    foreach ($source in $unmapped) {
        $sameApplication = @($MappingContext.targets | Where-Object {
            [int]$_.application -eq [int]$source.application
        })
        $sameName = @($sameApplication | Where-Object {
            [string]$_.sourceName -eq [string]$source.sourceName
        })
        $candidateSet = if ($sameName.Count -gt 0) { $sameName } else { $sameApplication }
        $deviceIds = @($candidateSet | Select-Object -ExpandProperty targetDeviceId -Unique)
        if ($deviceIds.Count -ne 1) {
            throw "Device mapping is ambiguous for source '$($source.sourceName)'. Open LiveStudio and select the target 4KPro manually."
        }

        $selected = @($candidateSet | Where-Object {
            [string]$_.targetDeviceId -eq [string]$deviceIds[0]
        } | Select-Object -First 1)[0]
        $payload = [ordered]@{
            snapshotId = $SnapshotId
            sourceLogicalId = [string]$source.sourceLogicalId
            application = [int]$source.application
            targetDeviceId = [string]$selected.targetDeviceId
            targetSourceName = [string]$selected.sourceName
        }
        [void](Invoke-LiveStudioAgent -Method 12 -Payload $payload -TimeoutSeconds 30)
        $decisions += [ordered]@{
            application = [int]$source.application
            sourceName = [string]$source.sourceName
            targetSourceName = [string]$selected.sourceName
            targetDeviceName = [string]$selected.deviceName
            targetDeviceId = [string]$selected.targetDeviceId
            rule = 'only-candidate-for-application'
        }
    }

    $finalContext = Invoke-LiveStudioAgent -Method 9 -Payload ([ordered]@{ snapshotId = $SnapshotId }) -TimeoutSeconds 30
    $remaining = @($finalContext.sources | Where-Object { $null -eq $_.mapping })
    if ($remaining.Count -gt 0) {
        throw "$($remaining.Count) device mapping(s) remain unresolved. Use the LiveStudio restore preparation UI."
    }

    return $decisions
}

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'This validation tool only runs on Windows.'
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $PSScriptRoot 'LiveStudio-CrossMachine-Test.lscfg'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'results'
}

$packageFullPath = [System.IO.Path]::GetFullPath($PackagePath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputFullPath | Out-Null
$outputPath = Join-Path $outputFullPath ($experimentId + '.json')

$report = [ordered]@{
    experimentId = $experimentId
    capturedAt = $startedAt.ToString('o')
    machine = [ordered]@{
        machineAlias = $machineAlias
        windowsVersion = ''
        captureDevices = @()
    }
    applications = [ordered]@{
        liveStudioVersion = ''
        liveStudioAgentVersion = ''
        obsVersion = ''
        obsRunning = $false
        liveCompanionVersion = ''
        liveCompanionRunning = $false
    }
    package = [ordered]@{
        path = $packageFullPath
        length = 0
        sha256 = ''
        expectedSha256 = $expectedPackageSha256
        snapshotId = ''
        snapshotName = ''
        signerFingerprintSha256 = ''
        signerTrusted = $false
    }
    installer = [ordered]@{
        path = ''
        version = ''
        length = 0
        sha256 = ''
        signatureStatus = ''
        selfTestExitCode = -1
    }
    preflight = [ordered]@{
        agentConnected = $false
        canCapture = $false
        canRestore = $false
        applications = @()
        mappingSourceCount = 0
        mappingTargetCount = 0
        mappingDecisions = @()
    }
    captureDiagnostic = [ordered]@{
        requested = [bool]$DiagnoseCapture
        snapshotCountBefore = 0
        snapshotCountAfter = 0
        succeeded = $false
        snapshotId = ''
        snapshotName = ''
        error = ''
        recentOperations = @()
    }
    cycles = @()
    observations = [ordered]@{
        readBackMatched = $false
        rollbackMatched = $false
        autoBackupCreatedEveryCycle = $false
        sensitivePaths = @()
    }
    result = 'Invalid'
    notes = ''
    error = ''
}

try {
    $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $report.machine.windowsVersion = "$($operatingSystem.Caption) $($operatingSystem.Version) build $($operatingSystem.BuildNumber)"
    $report.machine.captureDevices = @(Get-CaptureDeviceEvidence)

    $obs = Get-FileVersionFromCandidates -ProcessNames @('obs64') -FileCandidates @(
        "$env:ProgramFiles\obs-studio\bin\64bit\obs64.exe",
        "${env:ProgramFiles(x86)}\obs-studio\bin\64bit\obs64.exe")
    $report.applications.obsVersion = [string]$obs.version
    $report.applications.obsRunning = [bool]$obs.running

    $liveCompanion = Get-FileVersionFromCandidates -ProcessNames @('StreamingTool', 'webcast_mate') -FileCandidates @(
        'D:\webcast_mate\*\*.exe',
        "$env:LOCALAPPDATA\webcast_mate\*\*.exe") -PathPattern '(?i)webcast_mate'
    $report.applications.liveCompanionVersion = [string]$liveCompanion.version
    $report.applications.liveCompanionRunning = [bool]$liveCompanion.running

    $liveStudioPackage = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like '*LiveStudio*'
    } | Sort-Object Version -Descending | Select-Object -First 1)
    if ($liveStudioPackage.Count -gt 0) {
        $report.applications.liveStudioVersion = [string]$liveStudioPackage[0].Version
    }
    $agentProcess = @(Get-Process -Name 'LiveStudio.Agent' -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($agentProcess.Count -gt 0) {
        try {
            $report.applications.liveStudioAgentVersion = (Get-Item -LiteralPath $agentProcess[0].Path).VersionInfo.FileVersion
        }
        catch {
            $report.applications.liveStudioAgentVersion = ''
        }
    }

    if (-not (Test-Path -LiteralPath $packageFullPath -PathType Leaf)) {
        throw "Validation package does not exist: $packageFullPath"
    }
    $packageItem = Get-Item -LiteralPath $packageFullPath
    $packageHash = (Get-Sha256 -Path $packageFullPath).ToUpperInvariant()
    $report.package.length = [long]$packageItem.Length
    $report.package.sha256 = $packageHash
    if ($packageHash -ne $expectedPackageSha256) {
        throw "Validation package SHA-256 mismatch: $packageHash"
    }

    $installerCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($InstallerPath)) {
        $installerCandidates += [System.IO.Path]::GetFullPath($InstallerPath)
    }
    $installerCandidates += [System.IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'LiveStudio-Setup.exe'))
    $installerCandidates += [System.IO.Path]::GetFullPath((Join-Path (Split-Path (Split-Path $packageFullPath -Parent) -Parent) 'LiveStudio-Setup.exe'))
    $resolvedInstaller = @($installerCandidates | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    } | Select-Object -First 1)
    $installerPath = if ($resolvedInstaller.Count -gt 0) { $resolvedInstaller[0] } else { $installerCandidates[0] }
    $report.installer.path = $installerPath
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "LiveStudio installer does not exist. Checked: $($installerCandidates -join '; ')"
    }
    $installerItem = Get-Item -LiteralPath $installerPath
    $installerHash = (Get-Sha256 -Path $installerPath).ToUpperInvariant()
    try {
        $installerSignatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $installerPath).Status
    }
    catch {
        $installerSignatureStatus = 'PowerShell module unavailable; native installer self-test used'
    }
    $installerSelfTest = Start-Process -FilePath $installerPath -ArgumentList '--verify-only' -WindowStyle Hidden -Wait -PassThru
    $report.installer.version = [string]$installerItem.VersionInfo.FileVersion
    $report.installer.length = [long]$installerItem.Length
    $report.installer.sha256 = $installerHash
    $report.installer.signatureStatus = $installerSignatureStatus
    $report.installer.selfTestExitCode = [int]$installerSelfTest.ExitCode
    if ($installerHash -ne $expectedInstallerSha256 -or $installerSelfTest.ExitCode -ne 0) {
        throw 'LiveStudio installer failed SHA-256 or embedded self-test validation.'
    }

    $preview = Invoke-LiveStudioAgent -Method 13 -Payload ([ordered]@{ path = $packageFullPath }) -TimeoutSeconds 30
    $report.preflight.agentConnected = $true
    $report.package.snapshotId = [string]$preview.snapshotId
    $report.package.snapshotName = [string]$preview.name
    $report.package.signerFingerprintSha256 = ([string]$preview.signerFingerprintSha256).ToUpperInvariant()
    $report.package.signerTrusted = [bool]$preview.signerTrusted
    if ($report.package.signerFingerprintSha256 -ne $expectedSignerFingerprint) {
        throw "Validation package signer fingerprint mismatch: $($report.package.signerFingerprintSha256)"
    }

    $initialState = Invoke-LiveStudioAgent -Method 0 -Payload ([ordered]@{}) -TimeoutSeconds 30
    $report.preflight.canCapture = [bool]$initialState.canCapture
    $report.preflight.canRestore = [bool]$initialState.canRestore
    $report.preflight.applications = @(Convert-ApplicationStates -Applications $initialState.applications)
    foreach ($applicationState in @($initialState.applications)) {
        if ([int]$applicationState.application -eq 0) {
            $report.applications.obsVersion = [string]$applicationState.version
            $report.applications.obsRunning = [bool]$applicationState.isRunning
        }
        elseif ([int]$applicationState.application -eq 1) {
            $report.applications.liveCompanionVersion = [string]$applicationState.version
            $report.applications.liveCompanionRunning = [bool]$applicationState.isRunning
        }
    }

    $report.captureDiagnostic.snapshotCountBefore = @($initialState.snapshots).Count

    if ($DiagnoseCapture) {
        try {
            $capture = Invoke-LiveStudioAgent -Method 2 -Payload ([ordered]@{
                name = 'Capture diagnosis ' + [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss')
                cameraStations = $null
                imageChanges = $null
            }) -TimeoutSeconds 180
            $report.captureDiagnostic.succeeded = $true
            $report.captureDiagnostic.snapshotId = [string]$capture.snapshotId
            $report.captureDiagnostic.snapshotName = [string]$capture.name
        }
        catch {
            $report.captureDiagnostic.error = $_.Exception.Message
        }

        $captureState = Invoke-LiveStudioAgent -Method 0 -Payload ([ordered]@{}) -TimeoutSeconds 30
        $report.captureDiagnostic.snapshotCountAfter = @($captureState.snapshots).Count
        $report.captureDiagnostic.recentOperations = @(Convert-RecentOperations -State $captureState)
        if ($report.captureDiagnostic.succeeded) {
            if ($report.captureDiagnostic.snapshotCountAfter -ne $report.captureDiagnostic.snapshotCountBefore + 1) {
                throw 'Capture returned success but the managed snapshot count did not increase by exactly one.'
            }

            $report.result = 'EvidenceOnly'
            $report.notes = 'Production capture succeeded and created one signed managed snapshot. No restore was executed.'
        }
        else {
            if ($report.captureDiagnostic.snapshotCountAfter -ne $report.captureDiagnostic.snapshotCountBefore) {
                throw 'Capture failed but the managed snapshot count changed; package/index atomicity requires investigation.'
            }

            $report.result = 'EvidenceOnly'
            $report.notes = 'Production capture failure reproduced without adding a partial managed snapshot. The exact Agent error and recent operation records are included.'
        }
    }
    elseif (-not $ExecuteFiveRestoreCycles) {
        $report.result = 'EvidenceOnly'
        $report.notes = 'Read-only preflight completed. No OBS or Live Companion settings were changed.'
    }
    else {
        Write-Host ''
        Write-Host 'WARNING: this will import the signed package and restore OBS and Live Companion five times.' -ForegroundColor Yellow
        Write-Host 'Each cycle must create a permanent pre-restore backup and pass full read-back verification.' -ForegroundColor Yellow
        $confirmation = Read-Host 'Type RESTORE-5 to continue'
        if ($confirmation -cne 'RESTORE-5') {
            throw 'Restore validation was cancelled because the confirmation text did not match.'
        }

        if (-not $report.applications.obsRunning -or -not $report.applications.liveCompanionRunning) {
            throw 'OBS and Live Companion must both be running before the five-cycle validation starts.'
        }
        if ($report.applications.liveStudioVersion -ne '0.1.22.0' -or
            $report.applications.liveStudioAgentVersion -ne '0.1.22.0') {
            throw "LiveStudio Desktop and Agent must both be 0.1.22.0. Desktop=$($report.applications.liveStudioVersion), Agent=$($report.applications.liveStudioAgentVersion)."
        }

        $import = Invoke-LiveStudioAgent -Method 14 -Payload ([ordered]@{
            path = $packageFullPath
            trustSigner = $true
        }) -TimeoutSeconds 60
        $snapshotId = [string]$import.snapshotId
        if ($snapshotId -ne [string]$preview.snapshotId) {
            throw 'Imported snapshot ID does not match the inspected package ID.'
        }

        $mappingContext = Invoke-LiveStudioAgent -Method 9 -Payload ([ordered]@{ snapshotId = $snapshotId }) -TimeoutSeconds 60
        $report.preflight.mappingSourceCount = @($mappingContext.sources).Count
        $report.preflight.mappingTargetCount = @($mappingContext.targets).Count
        $report.preflight.mappingDecisions = @(Resolve-UniqueMappings -SnapshotId $snapshotId -MappingContext $mappingContext)

        $readyState = Invoke-LiveStudioAgent -Method 0 -Payload ([ordered]@{}) -TimeoutSeconds 30
        $report.preflight.canRestore = [bool]$readyState.canRestore
        if (-not $readyState.canRestore) {
            throw "LiveStudio Agent refused restore preflight: $($readyState.statusMessage)"
        }

        for ($cycle = 1; $cycle -le $CycleCount; $cycle++) {
            $cycleStartedAt = [DateTimeOffset]::Now
            $beforeState = Invoke-LiveStudioAgent -Method 0 -Payload ([ordered]@{}) -TimeoutSeconds 30
            $beforeCount = @($beforeState.snapshots).Count
            $cycleRecord = [ordered]@{
                cycle = $cycle
                startedAt = $cycleStartedAt.ToString('o')
                completedAt = ''
                success = $false
                message = ''
                operation = $null
                snapshotCountBefore = $beforeCount
                snapshotCountAfter = $beforeCount
                autoBackupCreated = $false
                applicationsAfter = @()
            }

            try {
                $restoreResult = Invoke-LiveStudioAgent -Method 3 -Payload ([ordered]@{
                    snapshotId = $snapshotId
                    currentCameraStations = $null
                }) -TimeoutSeconds 240
                $cycleRecord.success = $true
                $cycleRecord.message = 'Restore request succeeded and the Agent reported full read-back verification.'
            }
            catch {
                $cycleRecord.message = $_.Exception.Message
            }

            $afterState = Invoke-LiveStudioAgent -Method 0 -Payload ([ordered]@{}) -TimeoutSeconds 30
            $afterCount = @($afterState.snapshots).Count
            $cycleRecord.completedAt = [DateTimeOffset]::Now.ToString('o')
            $cycleRecord.snapshotCountAfter = $afterCount
            $cycleRecord.autoBackupCreated = ($afterCount -eq $beforeCount + 1)
            $cycleRecord.operation = Get-LatestOperationForSnapshot -State $afterState -SnapshotId $snapshotId
            $cycleRecord.applicationsAfter = @(Convert-ApplicationStates -Applications $afterState.applications)
            $report.cycles += $cycleRecord

            if (-not $cycleRecord.success) {
                throw "Restore cycle $cycle failed: $($cycleRecord.message)"
            }
            if (-not $cycleRecord.autoBackupCreated) {
                throw "Restore cycle $cycle did not create exactly one permanent pre-restore backup."
            }
            if ($null -eq $cycleRecord.operation -or [int]$cycleRecord.operation.status -ne 1) {
                throw "Restore cycle $cycle did not persist a Succeeded operation record."
            }
        }

        $report.observations.readBackMatched = $true
        $report.observations.autoBackupCreatedEveryCycle = $true
        $physicalDevices = @($report.machine.captureDevices | Where-Object { [bool]$_.physicalCandidate })
        if ($physicalDevices.Count -gt 0) {
            $report.result = 'Mapped'
            $report.notes = "All $CycleCount requested restore cycles succeeded through the production transaction path on a machine with a detected physical capture-device candidate. Formal Verified status still requires the repository's full multi-machine fault matrix."
        }
        else {
            $report.result = 'EvidenceOnly'
            $report.notes = "All $CycleCount requested restore cycles succeeded, but the hardware collector did not identify a physical capture device. Supply the exact hardware ID and driver evidence before using this run as cross-hardware evidence."
        }
    }
}
catch {
    $scriptExitCode = 1
    $report.error = $_.Exception.Message
    if ($report.cycles.Count -gt 0) {
        $report.result = 'EvidenceOnly'
        $report.observations.rollbackMatched = @($report.cycles | Where-Object {
            $null -ne $_.operation -and [int]$_.operation.status -eq 4
        }).Count -gt 0
    }
    else {
        $report.result = 'Invalid'
    }
}
finally {
    $report.capturedAt = [DateTimeOffset]::Now.ToString('o')
    Write-JsonFile -Value $report -Path $outputPath
    Write-Host ''
    Write-Host "Evidence report: $outputPath"
    Write-Host "Result: $($report.result)"
    if (-not [string]::IsNullOrWhiteSpace($report.error)) {
        Write-Host "Error: $($report.error)" -ForegroundColor Red
    }
}

exit $scriptExitCode
