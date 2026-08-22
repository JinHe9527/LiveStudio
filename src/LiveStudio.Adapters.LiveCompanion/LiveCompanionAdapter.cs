using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class LiveCompanionAdapter : IApplicationAdapter
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

        var liveState = await configurationStore.InspectLiveStateAsync(cancellationToken);
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
        var documents = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException(
                $"未在 {configurationStore.RootPath} 找到可识别的设备、画面模式或滤镜配置");
        }

        var sources = await LiveCompanionSnapshotProjector.CreateSourcesAsync(documents, cancellationToken);
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(documents);
        return new ApplicationSnapshot(
            ApplicationKind.LiveCompanion,
            NormalizeVersion(process?.Version),
            Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes)),
            sources,
            documents);
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
            return await CaptureAsync(cancellationToken);
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
        if (context.Snapshot.NativeDocuments.Count == 0)
        {
            return RestorePreflightResult.Fail(
                JobStatus.IncompatibleVersion,
                "存档中没有直播伴侣原生配置字段");
        }

        var current = await configurationStore.CaptureDocumentsAsync(cancellationToken);
        var currentByPath = current.ToDictionary(
            document => document.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
        foreach (var document in context.Snapshot.NativeDocuments)
        {
            if (!currentByPath.TryGetValue(document.RelativePath, out var target))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.IncompatibleVersion,
                    $"目标版本缺少配置文件 {document.RelativePath}");
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

    public Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IApplicationRestoreSession>(new LiveCompanionRestoreSession(
            configurationStore,
            context,
            LiveCompanionProcessController.FindRunning()));
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
        LiveCompanionProcessInfo? originalProcess) : IApplicationRestoreSession
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

            backup = await configurationStore.BackupAsync(context.Snapshot.NativeDocuments, cancellationToken);
            stopped = true;
        }

        public Task ApplyAsync(CancellationToken cancellationToken)
        {
            EnsureStopped();
            var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
            var assets = context.Snapshot.Sources
                .SelectMany(source => source.Filters)
                .SelectMany(filter => filter.Assets)
                .GroupBy(asset => asset.Sha256, StringComparer.Ordinal)
                .Select(group => group.First())
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
            if (originalProcess?.ExecutablePath is null)
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
                .GroupBy(asset => asset.Sha256, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var expected = LiveCompanionConfigurationStore.CreateExpectedDocuments(
                context.Snapshot.NativeDocuments,
                mappings,
                assets,
                context.AssetDirectory);
            var current = await configurationStore.CaptureDocumentsAsync(cancellationToken);
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

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            committed = true;
            backup = new Dictionary<string, byte[]>();
            return Task.CompletedTask;
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
            if (originalProcess?.ExecutablePath is not null)
            {
                await LiveCompanionProcessController.StartAsync(originalProcess.ExecutablePath, cancellationToken);
                await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);
            }
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
