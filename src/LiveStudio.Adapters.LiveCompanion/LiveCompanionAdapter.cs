using System.Text.Json;
using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapter(LiveCompanionAdapterCatalog adapterCatalog) : IApplicationAdapter
{
    private readonly LiveCompanionConfigurationStore configurationStore = new();

    public ApplicationKind Kind => ApplicationKind.LiveCompanion;

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

    public async Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken)
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
            cancellationToken);
    }

    private async Task<ApplicationSnapshot> CaptureConsistentFromDiskAsync(
        string version,
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await CaptureFromDiskAsync(version, wasRunning, cancellationToken);
            var second = await CaptureFromDiskAsync(version, wasRunning, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var discoveredDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var discoveredStructureFingerprint = LiveCompanionStructureFingerprint.Compute(discoveredDocuments);
        var match = adapterCatalog.Match(version, discoveredStructureFingerprint, discoveredDocuments);
        var structureFingerprint = match.Adapter?.Definition.StructureFingerprint
            ?? discoveredStructureFingerprint;
        var documents = match.Adapter is null
            ? discoveredDocuments
            : await configurationStore.CaptureDefinedDocumentsAsync(match.Adapter, cancellationToken);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                $"未在 {configurationStore.RootPath} 找到通过敏感字段排除检查的原生配置");
        }

        // A configuration document is not necessarily a video source or filter chain. Those semantics may
        // only be projected by an explicit signed mapping; discovery data remains in the native tree.
        IReadOnlyList<VideoSource> sources = [];
        var coverage = match.Adapter is null
            ? documents.SelectMany(document => document.Values.SelectMany(value =>
            {
                var presentation = LiveCompanionDiscoveryPresentation.Present(document, value);
                return EnumerateDiscoveryCoverage(
                    document,
                    value.Value,
                    $"{document.RelativePath}:{value.JsonPointer}",
                    presentation.Category,
                    presentation.NativeName,
                    presentation.UiPath);
            })).ToArray()
            : CreateDefinedCoverage(match.Adapter, documents);
        var configurationTree = LiveCompanionConfigurationTree.Create(documents, match.Adapter);
        IReadOnlyList<FilterChainSnapshot> filterChains = [];
        return new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            version,
            match.Adapter?.Definition.Id ?? "webcast-mate-json-discovery",
            match.Adapter?.DefinitionSha256 ?? string.Empty,
            structureFingerprint,
            match.Level switch
            {
                AdapterMatchLevel.Verified => CompatibilityLevel.Verified,
                AdapterMatchLevel.Experimental => CompatibilityLevel.Experimental,
                _ => CompatibilityLevel.Unsupported
            },
            wasRunning,
            coverage,
            sources,
            documents,
            configurationTree,
            filterChains);
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
        LiveCompanionConfigurationStore.IsRestorableField(field),
        LiveCompanionConfigurationStore.IsRestorableField(field),
        "SignedAdapterReadback",
        $"{field.Id}:{nativePath}",
        nativeName,
        uiPath,
        field.EvidenceStatus);

    private static string EscapePointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    public async Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
    {
        var process = LiveCompanionProcessController.FindRunning();
        if (process is null)
        {
            return await CaptureAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            throw new InvalidOperationException("无法读取直播伴侣启动路径，不会关闭应用");
        }

        await LiveCompanionProcessController.StopAsync(process.ProcessId, cancellationToken);
        try
        {
            return await CaptureConsistentFromDiskAsync(
                NormalizeVersion(process.Version),
                true,
                cancellationToken);
        }
        finally
        {
            await LiveCompanionProcessController.StartAsync(process.ExecutablePath, CancellationToken.None);
            await LiveCompanionProcessController.WaitUntilRunningAsync(CancellationToken.None);
        }
    }

    private static string ComputeCaptureHash(ApplicationSnapshot snapshot) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Version,
            snapshot.StructureFingerprint,
            snapshot.NativeDocuments
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

    public async Task<RestorePreflightResult> PreflightAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.Equals(context.Snapshot.AdapterId, "webcast-mate-json-discovery", StringComparison.Ordinal)
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

        var snapshotMatch = adapterCatalog.MatchSnapshot(
            runtime.Version,
            context.Snapshot.AdapterId,
            context.Snapshot.AdapterDefinitionSha256,
            context.Snapshot.StructureFingerprint);
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
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException)
        {
            return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion, exception.Message);
        }

        return RestorePreflightResult.Success;
    }

    public async Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalProcess = LiveCompanionProcessController.FindRunning();
        var executablePath = originalProcess?.ExecutablePath
            ?? LiveCompanionProcessController.FindInstalledExecutable()
            ?? throw new InvalidOperationException("找不到抖音直播伴侣安装程序");
        var restoreAdapter = adapterCatalog.GetAll().SingleOrDefault(adapter =>
            string.Equals(adapter.Definition.Id, context.Snapshot.AdapterId, StringComparison.Ordinal)
            && string.Equals(
                adapter.Definition.StructureFingerprint,
                context.Snapshot.StructureFingerprint,
                StringComparison.Ordinal)) ?? throw new InvalidOperationException("找不到存档对应的已签名直播伴侣适配器");
        var journal = await LiveCompanionTransactionJournal.CreateAsync(
            context.JobId,
            originalProcess,
            cancellationToken);
        return new LiveCompanionRestoreSession(
            configurationStore,
            context,
            originalProcess,
            executablePath,
            journal,
            restoreAdapter);
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
        LiveCompanionProcessInfo? originalProcess,
        string executablePath,
        LiveCompanionTransactionJournal journal,
        VerifiedAdapterDefinition restoreAdapter) : IApplicationRestoreSession
    {
        private readonly LiveCompanionCameraPayloadStore cameraPayloadStore =
            new(configurationStore.RootPath);
        private IReadOnlyDictionary<string, byte[]> backup = new Dictionary<string, byte[]>();
        private bool stopped;
        private bool createdCamera;

        public ApplicationKind Kind => ApplicationKind.LiveCompanion;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (originalProcess is not null)
            {
                await LiveCompanionProcessController.StopAsync(originalProcess.ProcessId, cancellationToken);
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
            LiveCompanionConfigurationStore.ValidateDefinedDocuments(
                restoreAdapter,
                context.Snapshot.NativeDocuments);
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = context.Snapshot.Sources
                .SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .ToArray();
            var writableDocuments = LiveCompanionConfigurationStore.SelectWritableDocuments(
                restoreAdapter,
                context.Snapshot.NativeDocuments);
            await configurationStore.ApplyAsync(
                writableDocuments,
                mappings,
                assets,
                context.AssetDirectory,
                cancellationToken);
            var sourceStore = writableDocuments.Single(document => string.Equals(
                Path.GetFileName(document.RelativePath),
                "sourceStore.json",
                StringComparison.OrdinalIgnoreCase));
            await cameraPayloadStore.ApplyAsync(sourceStore, cancellationToken);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            EnsureStopped();
            await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
            await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);
        }

        public async Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken)
        {
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = context.Snapshot.Sources
                .SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .ToArray();
            var writableDocuments = LiveCompanionConfigurationStore.SelectWritableDocuments(
                restoreAdapter,
                context.Snapshot.NativeDocuments);
            var expected = LiveCompanionConfigurationStore.CreateExpectedDocuments(
                writableDocuments,
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
            if (targets.Count != 1)
            {
                return new RestoreVerificationResult(
                    false,
                    [$"当前签名恢复要求存档恰好包含一个摄像头，实际为 {targets.Count} 个"]);
            }

            var target = targets[0];
            var packagePath = journal.GetWorkingPath("camera-effect.zip");
            await LiveCompanionEffectPackage.CreateAsync(
                packagePath,
                effectStore,
                target.EffectConfigurationId,
                cancellationToken);

            // 等待渲染进程完成 StoreCache -> MediaSDK 的初始化与反向同步，不能在旧来源
            // 尚未被原生层判定无效时误认为已经恢复。
            await Task.Delay(TimeSpan.FromSeconds(28), cancellationToken);
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
            else if (active.Length == 1)
            {
                // OBS 或其他应用单独发生变化时，直播伴侣可能已经与目标存档逐字段
                // 完全一致。先用与最终提交相同的完整验证器回读，完全一致时不打开
                // 摄像头设置、不重复导入效果包，也不让一次身份恢复依赖原生菜单。
                var existingDifferences = await LiveCompanionRestoreVerifier.VerifyAsync(
                    configurationStore.RootPath,
                    expected,
                    target,
                    active[0],
                    cancellationToken);
                if (existingDifferences.Count == 0)
                {
                    return new RestoreVerificationResult(true, []);
                }

                await LiveCompanionNativeUiRestorer.ImportEffectForExistingCameraAsync(
                    running.ProcessId,
                    packagePath,
                    cancellationToken);
            }
            else
            {
                return new RestoreVerificationResult(
                    false,
                    [$"直播伴侣中存在多个同名摄像头 {target.DeviceId}，无法安全选择恢复目标"]);
            }

            running = LiveCompanionProcessController.FindRunning()
                ?? throw new InvalidOperationException("直播伴侣在原生配置导入后意外退出");
            await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            await cameraPayloadStore.ApplyAsync(sourceStore, cancellationToken);
            await cameraPayloadStore.ApplyToActiveSourcesAsync(sourceStore, cancellationToken);
            await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
            await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(24), cancellationToken);
            var restored = (await cameraPayloadStore.GetActiveCamerasAsync(cancellationToken))
                .Where(camera => string.Equals(camera.DeviceId, target.DeviceId, StringComparison.Ordinal))
                .ToArray();
            if (restored.Length != 1)
            {
                return new RestoreVerificationResult(
                    false,
                    [$"重启后未找到唯一摄像头 {target.DeviceId}"]);
            }

            var differences = await LiveCompanionRestoreVerifier.VerifyAsync(
                configurationStore.RootPath,
                expected,
                target,
                restored[0],
                cancellationToken);
            return new RestoreVerificationResult(differences.Count == 0, differences);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (originalProcess is null
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
            if (originalProcess is not null)
            {
                await LiveCompanionProcessController.StartAsync(executablePath, cancellationToken);
                await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);
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
