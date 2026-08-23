using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapter(LiveCompanionAdapterCatalog adapterCatalog) : IApplicationAdapter
{
    private readonly LiveCompanionConfigurationStore configurationStore = new();

    public ApplicationKind Kind => ApplicationKind.LiveCompanion;

    public async Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var process = LiveCompanionProcessController.FindRunning();
        if (process is null)
        {
            return new ApplicationRuntimeStatus(false, false, false, "unknown", true);
        }

        if (LiveCompanionProcessController.TryInspectWindowLiveState(process.ProcessId, out var windowIsLive))
        {
            return new ApplicationRuntimeStatus(
                true,
                windowIsLive,
                false,
                NormalizeVersion(process.Version),
                true);
        }

        var discoveredDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var structureFingerprint = LiveCompanionStructureFingerprint.Compute(discoveredDocuments);
        var match = adapterCatalog.Match(NormalizeVersion(process.Version), structureFingerprint);
        var liveState = match.Adapter is null
            ? new LiveCompanionLiveState(false, false)
            : await configurationStore.InspectDefinedLiveStateAsync(match.Adapter, cancellationToken);
        return new ApplicationRuntimeStatus(
            true,
            liveState.IsLive,
            false,
            NormalizeVersion(process.Version),
            liveState.CanDetermine);
    }

    public async Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var process = LiveCompanionProcessController.FindRunning();
        return await CaptureFromDiskAsync(
            NormalizeVersion(process?.Version),
            process is not null,
            cancellationToken);
    }

    private async Task<ApplicationSnapshot> CaptureFromDiskAsync(
        string version,
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        var discoveredDocuments = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var structureFingerprint = LiveCompanionStructureFingerprint.Compute(discoveredDocuments);
        var match = adapterCatalog.Match(version, structureFingerprint);
        var documents = match.Adapter is null
            ? discoveredDocuments
            : await configurationStore.CaptureDefinedDocumentsAsync(match.Adapter, cancellationToken);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                $"未在 {configurationStore.RootPath} 找到可识别的设备、画面模式或滤镜配置");
        }

        var sources = await LiveCompanionSnapshotProjector.CreateSourcesAsync(documents, cancellationToken);
        var coverage = match.Adapter is null
            ? documents.SelectMany(document => document.Values.Select(value =>
                new CapturedParameterField(
                    $"{document.RelativePath}:{value.JsonPointer}",
                    value.Category,
                    value.Value.ValueKind.ToString(),
                    true,
                    false,
                    "DiscoveryReadOnly"))).ToArray()
            : CreateDefinedCoverage(match.Adapter, documents);
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
            documents);
    }

    private static CapturedParameterField[] CreateDefinedCoverage(
        VerifiedAdapterDefinition adapter,
        IReadOnlyList<NativeConfigurationDocument> documents)
    {
        var documentsByStore = documents.ToDictionary(document => document.StoreId, StringComparer.Ordinal);
        return adapter.Definition.Fields.OrderBy(field => field.StoreId, StringComparer.Ordinal)
            .ThenBy(field => field.NativePath, StringComparer.Ordinal)
            .Select(field =>
            {
                var prefix = documentsByStore.TryGetValue(field.StoreId, out var document)
                    ? document.RelativePath
                    : field.StoreId;
                return new CapturedParameterField(
                    $"{prefix}:{field.NativePath}",
                    field.UnifiedKind.ToString(),
                    field.ValueType,
                    field.Required,
                    field.Writable,
                    "SignedAdapterReadback");
            })
            .ToArray();
    }

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
            return await CaptureFromDiskAsync(
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
            || context.Snapshot.Compatibility == CompatibilityLevel.Unsupported)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "该直播伴侣存档来自探测扫描，没有签名适配定义，禁止写入");
        }

        var runtime = await InspectAsync(cancellationToken);
        var discovered = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var targetFingerprint = LiveCompanionStructureFingerprint.Compute(discovered);
        var targetMatch = adapterCatalog.Match(runtime.Version, targetFingerprint);
        if (targetMatch.Adapter is null)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                $"目标直播伴侣没有签名适配定义: {targetMatch.Reason}");
        }

        if (!string.Equals(
                context.Snapshot.AdapterId,
                targetMatch.Adapter.Definition.Id,
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
                targetMatch.Adapter,
                context.Snapshot.NativeDocuments);
        }
        catch (InvalidOperationException exception)
        {
            return RestorePreflightResult.Fail(JobStatus.IncompatibleVersion, exception.Message);
        }

        var current = await configurationStore.CaptureDefinedDocumentsAsync(
            targetMatch.Adapter,
            cancellationToken);
        var currentByStore = current.ToDictionary(document => document.StoreId, StringComparer.Ordinal);
        var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
        foreach (var document in context.Snapshot.NativeDocuments)
        {
            if (!currentByStore.TryGetValue(document.StoreId, out var target))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.IncompatibleVersion,
                    $"目标版本缺少配置存储 {document.StoreId}");
            }

            var targetPointers = target.Values.Select(value => value.JsonPointer).ToHashSet(StringComparer.Ordinal);
            var missingPointer = document.Values.FirstOrDefault(value => !targetPointers.Contains(value.JsonPointer));
            if (missingPointer is not null)
            {
                return RestorePreflightResult.Fail(
                    JobStatus.IncompatibleVersion,
                    $"目标版本缺少配置字段 {document.RelativePath}:{missingPointer.JsonPointer}");
            }

            if (document.Values.Any(value => value.Category == NativeParameterCategories.DeviceSelection)
                && !mappings.ContainsKey(document.SourceLogicalId))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.MappingRequired,
                    $"直播伴侣来源 {Path.GetFileNameWithoutExtension(document.RelativePath)} 尚未映射到目标设备");
            }
        }

        return RestorePreflightResult.Success;
    }

    public async Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalProcess = LiveCompanionProcessController.FindRunning();
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
        LiveCompanionTransactionJournal journal,
        VerifiedAdapterDefinition restoreAdapter) : IApplicationRestoreSession
    {
        private IReadOnlyDictionary<string, byte[]> backup = new Dictionary<string, byte[]>();
        private bool stopped;
        private bool committed;

        public ApplicationKind Kind => ApplicationKind.LiveCompanion;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (originalProcess is not null)
            {
                await LiveCompanionProcessController.StopAsync(originalProcess.ProcessId, cancellationToken);
            }

            stopped = true;
            backup = await configurationStore.BackupAsync(context.Snapshot.NativeDocuments, cancellationToken);
            await journal.SaveBackupsAsync(backup, cancellationToken);
        }

        public Task ApplyAsync(CancellationToken cancellationToken)
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
            return configurationStore.ApplyAsync(
                context.Snapshot.NativeDocuments,
                mappings,
                assets,
                context.AssetDirectory,
                cancellationToken);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            EnsureStopped();
            if (string.IsNullOrWhiteSpace(originalProcess?.ExecutablePath))
            {
                return;
            }

            await LiveCompanionProcessController.StartAsync(originalProcess.ExecutablePath, cancellationToken);
            await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);
        }

        public async Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken)
        {
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = context.Snapshot.Sources
                .SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .ToArray();
            var expected = LiveCompanionConfigurationStore.CreateExpectedDocuments(
                context.Snapshot.NativeDocuments,
                mappings,
                assets,
                context.AssetDirectory);
            var current = await configurationStore.CaptureDefinedDocumentsAsync(
                restoreAdapter,
                cancellationToken);
            var currentByPath = current.ToDictionary(
                document => document.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            var differences = new List<string>();
            foreach (var document in expected)
            {
                if (!currentByPath.TryGetValue(document.RelativePath, out var actualDocument))
                {
                    differences.Add($"缺少配置文件 {document.RelativePath}");
                    continue;
                }

                var actualValues = actualDocument.Values.ToDictionary(
                    value => value.JsonPointer,
                    StringComparer.Ordinal);
                foreach (var value in document.Values)
                {
                    if (!actualValues.TryGetValue(value.JsonPointer, out var actual)
                        || !string.Equals(value.Category, actual.Category, StringComparison.Ordinal)
                        || !JsonElement.DeepEquals(value.Value, actual.Value))
                    {
                        differences.Add($"{document.RelativePath}:{value.JsonPointer} 不一致");
                    }
                }
            }

            return new RestoreVerificationResult(differences.Count == 0, differences);
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            committed = true;
            backup = new Dictionary<string, byte[]>();
            await journal.CompleteAsync(cancellationToken);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (committed || !stopped)
            {
                return;
            }

            var running = LiveCompanionProcessController.FindRunning();
            if (running is not null)
            {
                await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            }

            await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, cancellationToken);
            if (!string.IsNullOrWhiteSpace(originalProcess?.ExecutablePath))
            {
                await LiveCompanionProcessController.StartAsync(originalProcess.ExecutablePath, cancellationToken);
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
