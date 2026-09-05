using System.Text.Json;
using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapter(
    LiveCompanionAdapterCatalog adapterCatalog,
    ILiveCompanionVideoDeviceCatalog? videoDeviceCatalog = null) : IApplicationAdapter
{
    private static readonly TimeSpan ReadbackMinimumObservation = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReadbackPollInterval = TimeSpan.FromMilliseconds(500);
    private const int RequiredStableReadbackCaptures = 2;
    internal const string ReadOnlyRecoveryBackupAdapterId = "webcast-mate-recovery-backup-readonly";
    private readonly LiveCompanionConfigurationStore configurationStore = new();

    public ApplicationKind Kind => ApplicationKind.LiveCompanion;

    // MediaSDK enums inspected in the signed 12.9.2 application; absent optional fields use native defaults.
    internal static bool IsSupportedColorMode(VideoMode mode) =>
        mode.ColorSpace is "" or "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8"
        && mode.ColorRange is "" or "0" or "1" or "2" or "3";

    public Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = LiveCompanionProcessController.FindRunning();
        if (process is null)
        {
            var executablePath = LiveCompanionProcessController.FindInstalledExecutable();
            var version = executablePath is null
                ? "unknown"
                : LiveCompanionProcessController.ResolveInstalledVersion(executablePath);
            return Task.FromResult(new ApplicationRuntimeStatus(
                false,
                false,
                false,
                NormalizeVersion(version),
                true));
        }

        if (LiveCompanionProcessController.TryInspectWindowLiveState(process.ProcessId, out var windowIsLive))
        {
            return Task.FromResult(new ApplicationRuntimeStatus(
                true,
                windowIsLive,
                false,
                NormalizeVersion(process.Version),
                true));
        }

        return Task.FromResult(new ApplicationRuntimeStatus(
            true,
            false,
            false,
            NormalizeVersion(process.Version),
            false));
    }

    public Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        CaptureAsync(false, cancellationToken);

    private async Task<ApplicationSnapshot> CaptureAsync(
        bool forRestoreBackup,
        CancellationToken cancellationToken)
    {
        var process = LiveCompanionProcessController.FindRunning();
        var executablePath = process?.ExecutablePath
            ?? LiveCompanionProcessController.FindInstalledExecutable();
        var version = process?.Version
            ?? (executablePath is null
                ? "unknown"
                : LiveCompanionProcessController.ResolveInstalledVersion(executablePath));
        return await CaptureConsistentFromDiskAsync(
            NormalizeVersion(version),
            process is not null,
            forRestoreBackup,
            cancellationToken);
    }

    private async Task<ApplicationSnapshot> CaptureConsistentFromDiskAsync(
        string version,
        bool wasRunning,
        bool forRestoreBackup,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await CaptureFromDiskAsync(version, wasRunning, forRestoreBackup, cancellationToken);
            var second = await CaptureFromDiskAsync(version, wasRunning, forRestoreBackup, cancellationToken);
            var firstHash = ComputeCaptureHash(first);
            var secondHash = ComputeCaptureHash(second);
            if (string.Equals(firstHash, secondHash, StringComparison.Ordinal))
            {
                return second with
                {
                    CaptureConsistency = new CaptureConsistency(
                        "NativeStoresDoubleRead",
                        firstHash,
                        secondHash,
                        attempt,
                        true)
                };
            }
        }

        throw new InvalidOperationException("直播伴侣配置在读取过程中持续变化，未生成不一致的半份存档");
    }

    private async Task<ApplicationSnapshot> CaptureFromDiskAsync(
        string version,
        bool wasRunning,
        bool forRestoreBackup,
        CancellationToken cancellationToken)
    {
        var discoveredDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var discoveredStructureFingerprint = LiveCompanionStructureFingerprint.Compute(discoveredDocuments);
        var match = adapterCatalog.Match(version, discoveredStructureFingerprint, discoveredDocuments);
        var structureFingerprint = match.Adapter?.Definition.StructureFingerprint
            ?? discoveredStructureFingerprint;
        var definedDocuments = match.Adapter is null
            ? discoveredDocuments
            : await configurationStore.CaptureDefinedDocumentsAsync(match.Adapter, cancellationToken);
        if (definedDocuments.Count == 0)
        {
            throw new InvalidOperationException(
                $"未在 {configurationStore.RootPath} 找到通过敏感字段排除检查的原生配置");
        }

        // 存档不能绑定来源电脑生成的场景、来源、效果与滤镜实例标识。即使当前电脑
        // 命中精确签名定义，持久化时仍保存经过敏感字段审计的完整原生树，并投影为
        // 可移植摄像头配置；签名定义只负责限定可写字段与目标版本结构。
        var portableProfile = LiveCompanionPortableProfile.TryCreate(
            discoveredDocuments,
            out var portableFailureReason);
        if (portableProfile is null)
        {
            if (forRestoreBackup)
            {
                return await CreateReadOnlyRecoveryBackupAsync(
                    version, wasRunning, discoveredDocuments, cancellationToken);
            }

            throw new InvalidOperationException(
                $"直播伴侣当前配置无法生成可跨电脑恢复的画面存档：{portableFailureReason}");
        }

        var portableMatch = match.Level == AdapterMatchLevel.Verified && match.Adapter is not null
            ? match
            : adapterCatalog.MatchPortableTarget(version, discoveredDocuments);
        if (portableMatch is not
            {
                Level: AdapterMatchLevel.Verified,
                Adapter: not null
            })
        {
            throw new InvalidOperationException(
                $"直播伴侣当前配置无法生成可跨电脑恢复的画面存档：{portableMatch.Reason}");
        }

        var documents = discoveredDocuments;
        IReadOnlyList<VideoSource> sources = [portableProfile.CreateVideoSource()];
        var coverage = portableProfile.CreateFieldCoverage();
        // Portable documents intentionally retain the complete discovery tree and its original
        // document identities. They are rebound through the signed adapter only during restore;
        // projecting them as signed stores here would collapse the four discovery documents that
        // share a probe StoreId and can create duplicate dictionary keys.
        var configurationTree = await LiveCompanionAssetMapper.CaptureAsync(
            LiveCompanionConfigurationTree.Create(documents, null),
            cancellationToken);
        IReadOnlyList<FilterChainSnapshot> filterChains = [];
        return new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            version,
            LiveCompanionPortableProfile.AdapterId,
            portableMatch.Adapter.DefinitionSha256,
            structureFingerprint,
            CompatibilityLevel.Experimental,
            wasRunning,
            coverage,
            sources,
            documents,
            configurationTree,
            filterChains);
    }

    internal static async Task<ApplicationSnapshot> CreateReadOnlyRecoveryBackupAsync(
        string version,
        bool wasRunning,
        IReadOnlyList<NativeConfigurationDocument> documents,
        CancellationToken cancellationToken)
    {
        // This archive preserves the audited pre-restore state even when the camera has been
        // deleted. It is never a writable target; native transaction journals still own rollback.
        var tree = await LiveCompanionAssetMapper.CaptureAsync(
            LiveCompanionConfigurationTree.Create(documents, null), cancellationToken);
        var coverage = documents.SelectMany(document => document.Values.SelectMany(value =>
            EnumerateDiscoveryCoverage(
                document, value.Value, $"{document.RelativePath}:{value.JsonPointer}",
                value.Category, value.JsonPointer, "恢复前只读备份"))).ToArray();
        return new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            version,
            ReadOnlyRecoveryBackupAdapterId,
            string.Empty,
            LiveCompanionStructureFingerprint.Compute(documents),
            CompatibilityLevel.Unsupported,
            wasRunning,
            coverage,
            [],
            documents,
            tree,
            []);
    }

    private static CapturedParameterField[] CreateDefinedCoverage(
        VerifiedAdapterDefinition adapter,
        IReadOnlyList<NativeConfigurationDocument> documents)
    {
        var documentsByStore = documents.ToDictionary(document => document.StoreId, StringComparer.Ordinal);
        return adapter.Definition.Fields.OrderBy(field => field.StoreId, StringComparer.Ordinal)
            .ThenBy(field => field.NativePath, StringComparer.Ordinal)
            .SelectMany(field =>
            {
                var prefix = documentsByStore.TryGetValue(field.StoreId, out var document)
                    ? document.RelativePath
                    : field.StoreId;
                var value = document?.Values.FirstOrDefault(candidate => candidate.JsonPointer == field.NativePath)?.Value;
                return value is null
                    ? []
                    : EnumerateDefinedCoverage(
                        field,
                        value.Value,
                        $"{prefix}:{field.NativePath}",
                        string.IsNullOrWhiteSpace(field.NativeName)
                            ? field.NativePath.Split('/').LastOrDefault() ?? field.NativePath
                            : field.NativeName,
                        field.UiPath).ToArray();
            })
            .ToArray();
    }

    private static IEnumerable<CapturedParameterField> EnumerateDiscoveryCoverage(
        NativeConfigurationDocument document,
        JsonElement value,
        string nativePath,
        string category,
        string nativeName,
        string uiPath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    foreach (var child in EnumerateDiscoveryCoverage(
                                 document,
                                 property.Value,
                                 $"{nativePath}/{EscapePointer(property.Name)}",
                                 category,
                                 property.Name,
                                 $"{uiPath}/{property.Name}"))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var child in EnumerateDiscoveryCoverage(
                             document,
                             item,
                             $"{nativePath}/{index}",
                             category,
                             index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                             $"{uiPath}/{index}"))
                {
                    yield return child;
                }

                index++;
            }

            if (index > 0)
            {
                yield break;
            }
        }

        yield return new CapturedParameterField(
            nativePath,
            category,
            value.ValueKind.ToString(),
            true,
            false,
            "DiscoveryReadOnly",
            $"live-companion:{document.StoreId}:{nativePath}",
            nativeName,
            uiPath,
            FieldEvidenceStatus.EvidenceOnly);
    }

    private static IEnumerable<CapturedParameterField> EnumerateDefinedCoverage(
        FieldMappingDefinition field,
        JsonElement value,
        string nativePath,
        string nativeName,
        string uiPath)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length > 0)
            {
                foreach (var property in properties)
                {
                    foreach (var child in EnumerateDefinedCoverage(
                                 field,
                                 property.Value,
                                 $"{nativePath}/{EscapePointer(property.Name)}",
                                 property.Name,
                                 $"{uiPath}/{property.Name}"))
                    {
                        yield return child;
                    }
                }

                yield break;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var child in EnumerateDefinedCoverage(
                             field,
                             item,
                             $"{nativePath}/{index}",
                             index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                             $"{uiPath}/{index}"))
                {
                    yield return child;
                }

                index++;
            }

            if (index > 0)
            {
                yield break;
            }
        }

        yield return CreateCoverageField(field, nativePath, nativeName, uiPath, value.ValueKind.ToString());
    }

    private static CapturedParameterField CreateCoverageField(
        FieldMappingDefinition field,
        string nativePath,
        string nativeName,
        string uiPath,
        string valueType) => new(
        nativePath,
        field.UnifiedKind.ToString(),
        valueType,
        LiveCompanionConfigurationStore.IsRequiredRestorableField(field),
        LiveCompanionConfigurationStore.IsRestorableField(field),
        "SignedAdapterReadback",
        $"{field.Id}:{nativePath}",
        nativeName,
        uiPath,
        field.EvidenceStatus);

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    public Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken) =>
        CaptureStableAsync(false, cancellationToken);

    public Task<ApplicationSnapshot> CaptureRestoreBackupAsync(CancellationToken cancellationToken) =>
        CaptureStableAsync(true, cancellationToken);

    private async Task<ApplicationSnapshot> CaptureStableAsync(
        bool forRestoreBackup,
        CancellationToken cancellationToken)
    {
        var process = LiveCompanionProcessController.FindRunning();
        if (process is null)
        {
            return await CaptureAsync(forRestoreBackup, cancellationToken);
        }

        var executablePath = process.ExecutablePath
            ?? throw new InvalidOperationException("无法读取直播伴侣启动路径，不会关闭应用");
        var version = string.Equals(process.Version, "unknown", StringComparison.OrdinalIgnoreCase)
            ? LiveCompanionProcessController.ResolveInstalledVersion(executablePath)
            : process.Version;

        await LiveCompanionProcessController.StopAsync(process.ProcessId, cancellationToken);
        try
        {
            return await CaptureConsistentFromDiskAsync(
                NormalizeVersion(version),
                true,
                forRestoreBackup,
                cancellationToken);
        }
        finally
        {
            await LiveCompanionProcessController.StartAsync(executablePath, CancellationToken.None);
            await LiveCompanionProcessController.WaitUntilRunningAsync(
                executablePath,
                CancellationToken.None);
        }
    }

    private static string ComputeCaptureHash(ApplicationSnapshot snapshot) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Version,
            snapshot.StructureFingerprint,
            snapshot.NativeDocuments,
            Assets = SnapshotAssetBindings.Collect([snapshot])
        })));

    public Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()
            || LiveCompanionProcessController.FindRunning() is not { } process
            || LiveCompanionProcessController.CaptureWindowPng(process.ProcessId) is not { } content)
        {
            return Task.FromResult<PreviewCapture?>(null);
        }

        return Task.FromResult<PreviewCapture?>(new PreviewCapture(
            ApplicationKind.LiveCompanion,
            "image/png",
            content,
            DateTimeOffset.UtcNow));
    }

    public Task<IApplicationRuntimeLease> PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IApplicationRuntimeLease>(
            new PassiveApplicationRuntimeLease(LiveCompanionProcessController.FindRunning() is not null));
    }

    public ApplicationSnapshot PrepareRestoreSnapshot(ApplicationSnapshot snapshot)
    {
        if (snapshot.AdapterId == ReadOnlyRecoveryBackupAdapterId)
        {
            return snapshot;
        }

        if (snapshot.Kind != ApplicationKind.LiveCompanion || snapshot.NativeDocuments.Count == 0)
        {
            return snapshot;
        }

        var portableProfile = LiveCompanionPortableProfile.TryCreate(snapshot.NativeDocuments);
        var sources = snapshot.Sources.Count == 0 && portableProfile is not null
            ? new[] { portableProfile.CreateVideoSource() }
            : snapshot.Sources;
        if (string.Equals(
                snapshot.AdapterId,
                LiveCompanionPortableProfile.AdapterId,
                StringComparison.Ordinal))
        {
            return ReferenceEquals(sources, snapshot.Sources)
                ? snapshot
                : snapshot with { Sources = sources };
        }

        if (portableProfile is not null)
        {
            var portableMatch = adapterCatalog.MatchPortableTarget(
                snapshot.Version,
                snapshot.NativeDocuments);
            if (portableMatch.Level == AdapterMatchLevel.Verified
                && portableMatch.Adapter is not null)
            {
                return snapshot with
                {
                    AdapterId = LiveCompanionPortableProfile.AdapterId,
                    AdapterDefinitionSha256 = portableMatch.Adapter.DefinitionSha256,
                    Compatibility = CompatibilityLevel.Experimental,
                    Sources = sources,
                    FieldCoverage = portableProfile.CreateFieldCoverage()
                };
            }
        }

        if (!string.Equals(snapshot.AdapterId, "webcast-mate-json-discovery", StringComparison.Ordinal))
        {
            return ReferenceEquals(sources, snapshot.Sources)
                ? snapshot
                : snapshot with { Sources = sources };
        }

        var match = adapterCatalog.Match(
            snapshot.Version,
            snapshot.StructureFingerprint,
            snapshot.NativeDocuments);
        if (match.Level != AdapterMatchLevel.Verified
            || match.Adapter is null
            || !LiveCompanionAdapterCatalog.MatchesCompatibleShape(
                match.Adapter.Definition,
                snapshot.NativeDocuments))
        {
            return snapshot;
        }

        var documents = LiveCompanionConfigurationStore.CreateDefinedDocuments(
            match.Adapter,
            snapshot.NativeDocuments);
        LiveCompanionConfigurationStore.ValidateDefinedDocuments(match.Adapter, documents);
        var tree = LiveCompanionConfigurationTree.Create(documents, match.Adapter);
        if (!tree.HasCompleteUiInventory
            || !tree.HasCompleteNativeInventory
            || tree.UnknownCount != 0
            || tree.EvidenceOnlyCount != 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            AdapterId = match.Adapter.Definition.Id,
            AdapterDefinitionSha256 = match.Adapter.DefinitionSha256,
            StructureFingerprint = match.Adapter.Definition.StructureFingerprint,
            Compatibility = CompatibilityLevel.Verified,
            FieldCoverage = CreateDefinedCoverage(match.Adapter, documents),
            NativeDocuments = documents,
            ConfigurationTree = tree
        };
    }

    public async Task<RestorePreflightResult> PreflightAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Snapshot.AdapterId == ReadOnlyRecoveryBackupAdapterId)
        {
            return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion,
                "这是恢复前不完整画面的只读备份，仅供检查；不能作为画面恢复目标");
        }

        var assets = SnapshotAssetBindings.Collect([context.Snapshot]);
        var missingAsset = LiveCompanionAssetMapper.FindUnresolvedAssetPaths(
                context.Snapshot.NativeDocuments,
                assets)
            .FirstOrDefault();
        if (missingAsset is not null)
        {
            return RestorePreflightResult.Fail(
                JobStatus.MissingAsset,
                $"直播伴侣滤镜素材不存在，且存档未包含对应文件: {Path.GetFileName(missingAsset)}");
        }

        var portableProfile = LiveCompanionPortableProfile.TryCreate(
            context.Snapshot.NativeDocuments);
        if (portableProfile is not null)
        {
            var portableRuntime = await InspectAsync(cancellationToken);
            var portableTargetDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
            var targetMatch = adapterCatalog.MatchPortableTarget(
                portableRuntime.Version,
                portableTargetDocuments);
            if (targetMatch.Level != AdapterMatchLevel.Verified || targetMatch.Adapter is null)
            {
                return RestorePreflightResult.Fail(
                    JobStatus.IncompatibleVersion,
                    $"目标直播伴侣版本或摄像头存储结构不受支持: {targetMatch.Reason}");
            }

            var signedVersion = CompatibilityMatcher.MatchCandidates(
                portableRuntime.Version, [targetMatch.Adapter], "可移植恢复版本");
            if (signedVersion.Level != AdapterMatchLevel.Verified
                && !LiveCompanionAdapterCatalog.MatchesCompatibleShape(targetMatch.Adapter.Definition, portableTargetDocuments))
            {
                return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion,
                    "目标直播伴侣版本不在签名恢复范围，且未通过完整字段结构匹配；允许保存读取，禁止写入");
            }

            var targetDeviceId = portableProfile.ResolveTargetDeviceId(
                context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId));
            if (string.IsNullOrWhiteSpace(targetDeviceId))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.MappingRequired,
                    "请选择存档摄像头在这台电脑上对应的视频设备");
            }

            var mode = portableProfile.CreateVideoSource().Mode;
            if (portableProfile.FindTargetTypeMismatch(portableTargetDocuments) is { } incompatiblePath)
            {
                return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion,
                    $"目标画面参数类型与存档不兼容：{incompatiblePath}");
            }
            if (videoDeviceCatalog is null || mode is null)
            {
                return RestorePreflightResult.Fail(JobStatus.MappingRequired, "无法验证目标视频设备能力，尚未写入画面配置");
            }
            if (!IsSupportedColorMode(mode))
            {
                return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion, "存档的色彩空间或色彩范围不在已确认的直播伴侣枚举范围，尚未写入配置");
            }
            var deviceCheck = await videoDeviceCatalog.ValidateAsync(targetDeviceId, mode, cancellationToken);
            if (!deviceCheck.CanProceed) { return deviceCheck; }

            try
            {
                await configurationStore.ValidateRepairTargetAsync(
                    targetMatch.Adapter,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
            {
                var status = exception is UnauthorizedAccessException
                    ? JobStatus.FailedRolledBack
                    : JobStatus.IncompatibleVersion;
                return RestorePreflightResult.Fail(status, exception.Message);
            }

            return RestorePreflightResult.Success;
        }

        if (string.Equals(context.Snapshot.AdapterId, "webcast-mate-json-discovery", StringComparison.Ordinal)
            || string.Equals(context.Snapshot.AdapterId, LiveCompanionPortableProfile.AdapterId, StringComparison.Ordinal)
            || context.Snapshot.Compatibility != CompatibilityLevel.Verified)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "该直播伴侣存档来自探测扫描，没有签名适配定义，禁止写入");
        }

        if (context.Snapshot.ConfigurationTree is not
            {
                HasCompleteUiInventory: true,
                HasCompleteNativeInventory: true,
                UnknownCount: 0,
                EvidenceOnlyCount: 0
            })
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "直播伴侣字段矩阵尚未完成 UI 与原生存储双向映射，禁止写入");
        }

        var runtime = await InspectAsync(cancellationToken);
        var targetDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);

        var snapshotMatch = adapterCatalog.MatchSnapshot(
            runtime.Version,
            context.Snapshot.AdapterId,
            context.Snapshot.AdapterDefinitionSha256,
            context.Snapshot.StructureFingerprint,
            targetDocuments);
        if (snapshotMatch.Level != AdapterMatchLevel.Verified || snapshotMatch.Adapter is null)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                $"目标直播伴侣与存档签名定义不匹配: {snapshotMatch.Reason}");
        }

        var restoreAdapter = snapshotMatch.Adapter;
        var targetFingerprint = restoreAdapter.Definition.StructureFingerprint;

        if (!string.Equals(
                context.Snapshot.AdapterId,
                restoreAdapter.Definition.Id,
                StringComparison.Ordinal)
            || !string.Equals(
                context.Snapshot.AdapterDefinitionSha256,
                restoreAdapter.DefinitionSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                context.Snapshot.StructureFingerprint,
                targetFingerprint,
                StringComparison.Ordinal))
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "存档与目标直播伴侣不是同一份已签名结构定义");
        }

        if (context.Snapshot.NativeDocuments.Count == 0)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "存档中没有直播伴侣原生配置字段");
        }

        try
        {
            LiveCompanionConfigurationStore.ValidateDefinedDocuments(
                restoreAdapter,
                context.Snapshot.NativeDocuments);
            await configurationStore.ValidateRepairTargetAsync(restoreAdapter, cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
        {
            var status = exception is UnauthorizedAccessException
                ? JobStatus.FailedRolledBack
                : JobStatus.IncompatibleVersion;
            return RestorePreflightResult.Fail(status, exception.Message);
        }

        return RestorePreflightResult.Success;
    }

    public async Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Snapshot.AdapterId == ReadOnlyRecoveryBackupAdapterId)
        {
            throw new InvalidOperationException("恢复前只读备份禁止写入");
        }
        var detectedProcess = LiveCompanionProcessController.FindRunning();
        var executablePath = detectedProcess is null
            ? LiveCompanionProcessController.FindInstalledExecutable()
              ?? throw new InvalidOperationException("找不到抖音直播伴侣安装程序")
            : detectedProcess.ExecutablePath
              ?? throw new InvalidOperationException("无法确认正在运行的抖音直播伴侣版本，配置没有写入");
        var processAtPreparation = detectedProcess is null
            ? null
            : detectedProcess with
            {
                ExecutablePath = executablePath,
                Version = string.Equals(
                    detectedProcess.Version,
                    "unknown",
                    StringComparison.OrdinalIgnoreCase)
                    ? LiveCompanionProcessController.ResolveInstalledVersion(executablePath)
                    : detectedProcess.Version
            };
        var portableProfile = LiveCompanionPortableProfile.TryCreate(context.Snapshot.NativeDocuments);
        VerifiedAdapterDefinition restoreAdapter;
        if (portableProfile is not null)
        {
            var runtime = await InspectAsync(cancellationToken);
            var targetDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
            var targetMatch = adapterCatalog.MatchPortableTarget(runtime.Version, targetDocuments);
            restoreAdapter = targetMatch.Level == AdapterMatchLevel.Verified
                             && targetMatch.Adapter is not null
                ? targetMatch.Adapter
                : throw new InvalidOperationException(
                    $"目标直播伴侣版本或摄像头存储结构不受支持: {targetMatch.Reason}");
        }
        else
        {
            restoreAdapter = adapterCatalog.GetAll().SingleOrDefault(adapter =>
                string.Equals(adapter.Definition.Id, context.Snapshot.AdapterId, StringComparison.Ordinal)
                && string.Equals(
                    adapter.Definition.StructureFingerprint,
                    context.Snapshot.StructureFingerprint,
                    StringComparison.Ordinal)) ?? throw new InvalidOperationException("找不到存档对应的已签名直播伴侣适配器");
        }
        var wasRunningBefore = context.ApplicationWasRunningBeforeRestore ?? processAtPreparation is not null;
        var journal = await LiveCompanionTransactionJournal.CreateAsync(
            context.JobId,
            wasRunningBefore ? processAtPreparation : null,
            cancellationToken);
        return new LiveCompanionRestoreSession(
            configurationStore,
            context,
            processAtPreparation,
            wasRunningBefore,
            executablePath,
            journal,
            restoreAdapter,
            portableProfile);
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var numeric = new string(version.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray())
            .TrimEnd('.');
        return Version.TryParse(numeric, out var parsed) ? parsed.ToString() : version;
    }

    private sealed class LiveCompanionRestoreSession(
        LiveCompanionConfigurationStore configurationStore,
        RestoreExecutionContext context,
        LiveCompanionProcessInfo? processAtPreparation,
        bool wasRunningBefore,
        string executablePath,
        LiveCompanionTransactionJournal journal,
        VerifiedAdapterDefinition restoreAdapter,
        LiveCompanionPortableProfile? portableProfile) : IApplicationRestoreSession
    {
        private readonly LiveCompanionCameraPayloadStore cameraPayloadStore =
            new(configurationStore.RootPath);
        private IReadOnlyDictionary<string, byte[]> backup = new Dictionary<string, byte[]>();
        private bool stopped;
        private bool createdCamera;

        public ApplicationKind Kind => ApplicationKind.LiveCompanion;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (processAtPreparation is not null)
            {
                await LiveCompanionProcessController.StopAsync(processAtPreparation.ProcessId, cancellationToken);
            }

            stopped = true;
            var files = new Dictionary<string, byte[]>(
                await configurationStore.BackupAsync(context.Snapshot.NativeDocuments, cancellationToken),
                StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(cameraPayloadStore.Path))
            {
                throw new FileNotFoundException(
                    "直播伴侣权威摄像头配置不存在",
                    cameraPayloadStore.Path);
            }

            files[cameraPayloadStore.Path] = await File.ReadAllBytesAsync(
                cameraPayloadStore.Path,
                cancellationToken);
            backup = files;
            await journal.SaveBackupsAsync(backup, cancellationToken);
        }

        public async Task ApplyAsync(CancellationToken cancellationToken)
        {
            EnsureStopped();
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = SnapshotAssetBindings.Collect([context.Snapshot]);
            IReadOnlyList<NativeConfigurationDocument> writableDocuments;
            if (portableProfile is not null)
            {
                writableDocuments = portableProfile.CreateExpectedDocuments(
                    restoreAdapter,
                    mappings,
                    assets,
                    context.AssetDirectory);
            }
            else
            {
                LiveCompanionConfigurationStore.ValidateDefinedDocuments(
                    restoreAdapter,
                    context.Snapshot.NativeDocuments);
                writableDocuments = LiveCompanionConfigurationStore.SelectWritableDocuments(
                    restoreAdapter,
                    context.Snapshot.NativeDocuments);
                await configurationStore.ApplyAsync(
                    writableDocuments,
                    mappings,
                    assets,
                    context.AssetDirectory,
                    cancellationToken);
            }

            var sourceStore = writableDocuments.Single(document => string.Equals(
                Path.GetFileName(document.RelativePath),
                "sourceStore.json",
                StringComparison.OrdinalIgnoreCase));
            if (portableProfile is not null)
            {
                await cameraPayloadStore.ApplyPortableAsync(sourceStore, cancellationToken);
                var targetDeviceId = portableProfile.ResolveTargetDeviceId(mappings);
                var activeTargets = (await cameraPayloadStore.GetActiveCamerasAsync(cancellationToken))
                    .Where(camera => string.Equals(camera.DeviceId, targetDeviceId, StringComparison.Ordinal))
                    .ToArray();
                await configurationStore.ApplyPortableBoundDocumentsAsync(
                    writableDocuments,
                    portableProfile.Camera,
                    activeTargets,
                    cancellationToken);
            }
            else
            {
                await cameraPayloadStore.ApplyAsync(sourceStore, cancellationToken);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            EnsureStopped();
            await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
            await LiveCompanionProcessController.WaitUntilRunningAsync(executablePath, cancellationToken);
        }

        public async Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken)
        {
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = SnapshotAssetBindings.Collect([context.Snapshot]);
            var expected = portableProfile is not null
                ? portableProfile.CreateExpectedDocuments(
                    restoreAdapter,
                    mappings,
                    assets,
                    context.AssetDirectory)
                : LiveCompanionConfigurationStore.CreateExpectedDocuments(
                    LiveCompanionConfigurationStore.SelectWritableDocuments(
                        restoreAdapter,
                        context.Snapshot.NativeDocuments),
                    mappings,
                    assets,
                    context.AssetDirectory);
            var sourceStore = expected.Single(document => string.Equals(
                Path.GetFileName(document.RelativePath),
                "sourceStore.json",
                StringComparison.OrdinalIgnoreCase));
            var effectStore = expected.Single(document => string.Equals(
                Path.GetFileName(document.RelativePath),
                "effectConfigStore.json",
                StringComparison.OrdinalIgnoreCase));
            var targets = await LiveCompanionCameraPayloadStore.GetTargetsAsync(
                sourceStore,
                cancellationToken);
            var equivalentTargets = targets
                .GroupBy(
                    target => $"{target.DeviceId}\0{target.EffectConfigurationId}\0{target.Payload.ToJsonString()}",
                    StringComparer.Ordinal)
                .ToArray();
            if (targets.Count == 0 || equivalentTargets.Length != 1)
            {
                return new RestoreVerificationResult(
                    false,
                    [$"存档中的 {targets.Count} 个摄像头来源没有共享同一设备、效果配置与画面参数"]);
            }

            var target = equivalentTargets[0].First();
            var packagePath = journal.GetWorkingPath("camera-effect.zip");
            await LiveCompanionEffectPackage.CreateAsync(
                packagePath,
                effectStore,
                target.EffectConfigurationId,
                cancellationToken);

            // StoreCache -> MediaSDK 的初始化与反向同步完成时间随电脑性能变化。
            // 连续读取四份原生存储，稳定后立即回读；超时仍沿用原来的 28 秒安全门，
            // 不能只因进程已出现就把尚未落稳的字段判定为恢复成功。
            await configurationStore.WaitForStableStoreFilesAsync(
                restoreAdapter,
                ReadbackMinimumObservation,
                TimeSpan.FromSeconds(28),
                ReadbackPollInterval,
                RequiredStableReadbackCaptures,
                cancellationToken);
            var running = LiveCompanionProcessController.FindRunning()
                ?? throw new InvalidOperationException("直播伴侣在原生恢复前意外退出");
            var active = (await cameraPayloadStore.GetActiveCamerasAsync(cancellationToken))
                .Where(camera => string.Equals(camera.DeviceId, target.DeviceId, StringComparison.Ordinal))
                .ToArray();
            if (active.Length == 0)
            {
                createdCamera = true;
                await LiveCompanionNativeUiRestorer.AddCameraAndImportEffectAsync(
                    running.ProcessId,
                    packagePath,
                    cancellationToken);
            }
            else
            {
                // 直播伴侣可能在其他场景保留同一设备的摄像头实例。不能仅凭设备名
                // 任选一个；逐个执行完整回读，任一实例与目标完全一致即可证明当前
                // 存档已经落地，无需再打开原生菜单或重复导入效果包。
                foreach (var candidate in active)
                {
                    var existingDifferences = await LiveCompanionRestoreVerifier.VerifyAsync(
                        configurationStore.RootPath,
                        expected,
                        target,
                        candidate,
                        cancellationToken);
                    if (existingDifferences.Count == 0)
                    {
                        return new RestoreVerificationResult(true, []);
                    }
                }

                var activeEffectGroups = active
                    .GroupBy(camera => camera.EffectConfigurationId, StringComparer.Ordinal)
                    .ToArray();
                if (activeEffectGroups.Length == 1)
                {
                    await LiveCompanionNativeUiRestorer.ImportEffectForExistingCameraAsync(
                        running.ProcessId,
                        packagePath,
                        cancellationToken);
                }
                else
                {
                    return new RestoreVerificationResult(
                        false,
                        [$"直播伴侣中存在 {active.Length} 个同名摄像头 {target.DeviceId}，但它们引用了不同效果配置，无法安全选择"]);
                }
            }

            running = LiveCompanionProcessController.FindRunning()
                ?? throw new InvalidOperationException("直播伴侣在原生配置导入后意外退出");
            var boundTargets = (await cameraPayloadStore.GetActiveCamerasAsync(cancellationToken))
                .Where(camera => string.Equals(camera.DeviceId, target.DeviceId, StringComparison.Ordinal))
                .ToArray();
            if (boundTargets.Length == 0)
            {
                return new RestoreVerificationResult(
                    false,
                    [$"原生导入后没有找到摄像头 {target.DeviceId}"]);
            }

            await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            if (portableProfile is not null)
            {
                await cameraPayloadStore.ApplyPortableAsync(sourceStore, cancellationToken);
                await cameraPayloadStore.ApplyPortableToActiveSourcesAsync(sourceStore, cancellationToken);
                await configurationStore.ApplyPortableBoundDocumentsAsync(
                    expected,
                    portableProfile.Camera,
                    boundTargets,
                    cancellationToken);
            }
            else
            {
                await cameraPayloadStore.ApplyAsync(sourceStore, cancellationToken);
                await cameraPayloadStore.ApplyToActiveSourcesAsync(sourceStore, cancellationToken);
            }
            await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
            await LiveCompanionProcessController.WaitUntilRunningAsync(executablePath, cancellationToken);

            await configurationStore.WaitForStableStoreFilesAsync(
                restoreAdapter,
                ReadbackMinimumObservation,
                TimeSpan.FromSeconds(24),
                ReadbackPollInterval,
                RequiredStableReadbackCaptures,
                cancellationToken);
            var restored = (await cameraPayloadStore.GetActiveCamerasAsync(cancellationToken))
                .Where(camera => string.Equals(camera.DeviceId, target.DeviceId, StringComparison.Ordinal))
                .ToArray();
            if (restored.Length == 0)
            {
                return new RestoreVerificationResult(
                    false,
                    [$"重启后未找到摄像头 {target.DeviceId}"]);
            }

            var allDifferences = new List<string>();
            foreach (var restoredCamera in restored)
            {
                var differences = await LiveCompanionRestoreVerifier.VerifyAsync(
                    configurationStore.RootPath,
                    expected,
                    target,
                    restoredCamera,
                    cancellationToken);
                allDifferences.AddRange(differences.Select(difference =>
                    $"{restoredCamera.SceneId}/{restoredCamera.SourceId}: {difference}"));
            }

            return new RestoreVerificationResult(allDifferences.Count == 0, allDifferences);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!wasRunningBefore
                && LiveCompanionProcessController.FindRunning() is { } running)
            {
                await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            }

            // 内存备份保留到 DisposeAsync，使另一应用随后的提交失败时仍可执行
            // 跨应用回滚；RollbackAsync 不以单应用提交为拒绝条件。
        }

        public Task CompleteAsync(CancellationToken cancellationToken) =>
            journal.CompleteAsync(cancellationToken);

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (!stopped)
            {
                await journal.CompleteAsync(cancellationToken);
                return;
            }

            var running = LiveCompanionProcessController.FindRunning();
            if (createdCamera && running is not null)
            {
                try
                {
                    await LiveCompanionNativeUiRestorer.DeleteSingleCameraAsync(
                        running.ProcessId,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // 文件级备份仍必须恢复；启动后的逐字段检查会阻止错误状态提交。
                }
            }

            running = LiveCompanionProcessController.FindRunning();
            if (running is not null)
            {
                await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            }

            await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, cancellationToken);
            if (wasRunningBefore)
            {
                await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
                await LiveCompanionProcessController.WaitUntilRunningAsync(executablePath, cancellationToken);
            }

            await journal.CompleteAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            backup = new Dictionary<string, byte[]>();
            return ValueTask.CompletedTask;
        }

        private void EnsureStopped()
        {
            if (!stopped)
            {
                throw new InvalidOperationException("直播伴侣尚未停止，不允许修改配置");
            }
        }
    }
}
