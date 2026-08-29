using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.Obs;

public sealed class ObsAdapter(
    IObsConnectionOptionsProvider optionsProvider,
    IObsCredentialProvider credentialProvider,
    IObsDeviceCatalog deviceCatalog,
    IObsAssetPathResolver? assetPathResolver = null) : IApplicationAdapter
{
    public ApplicationKind Kind => ApplicationKind.Obs;

    public async Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await CreateClientAsync(cancellationToken);
            var version = await client.CallAsync("GetVersion", null, cancellationToken);
            var stream = await client.CallAsync("GetStreamStatus", null, cancellationToken);
            var record = await client.CallAsync("GetRecordStatus", null, cancellationToken);
            return new ApplicationRuntimeStatus(
                true,
                stream.GetProperty("outputActive").GetBoolean(),
                record.GetProperty("outputActive").GetBoolean(),
                version.GetProperty("obsVersion").GetString() ?? "unknown",
                true);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            var running = ObsProcessController.FindRunning() is not null;
            return new ApplicationRuntimeStatus(running, false, false, "unknown", !running);
        }
    }

    public async Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var first = await CaptureOnceAsync(cancellationToken);
            var second = await CaptureOnceAsync(cancellationToken);
            var firstHash = ComputeCaptureHash(first);
            var secondHash = ComputeCaptureHash(second);
            if (string.Equals(firstHash, secondHash, StringComparison.Ordinal))
            {
                return second with
                {
                    CaptureConsistency = new CaptureConsistency(
                        "ObsWebSocketDoubleRead",
                        firstHash,
                        secondHash,
                        attempt,
                        true)
                };
            }
        }

        throw new InvalidOperationException("OBS 配置在读取过程中持续变化，未生成不一致的半份存档");
    }

    private async Task<ApplicationSnapshot> CaptureOnceAsync(CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        return await ObsSnapshotMapper.CaptureAsync(client, assetPathResolver, cancellationToken);
    }

    private static string ComputeCaptureHash(ApplicationSnapshot snapshot) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Version,
            snapshot.StructureFingerprint,
            snapshot.Sources
        })));

    public async Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
    {
        var original = ObsProcessController.FindRunning();
        var temporary = original is null ? await ObsProcessController.StartAsync(cancellationToken) : null;
        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return (await CaptureAsync(cancellationToken)) with { WasRunning = original is not null };
        }
        finally
        {
            if (temporary is not null)
            {
                await ObsProcessController.StopAsync(temporary.ProcessId, CancellationToken.None);
            }
        }
    }

    public async Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken)
    {
        var original = ObsProcessController.FindRunning();
        var temporary = original is null ? await ObsProcessController.StartAsync(cancellationToken) : null;
        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return await CapturePreviewConnectedAsync(cancellationToken);
        }
        finally
        {
            if (temporary is not null)
            {
                await ObsProcessController.StopAsync(temporary.ProcessId, CancellationToken.None);
            }
        }
    }

    public async Task<IApplicationRuntimeLease> PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        var original = ObsProcessController.FindRunning();
        if (original is not null)
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return new PassiveApplicationRuntimeLease(true);
        }

        var temporary = await ObsProcessController.StartAsync(cancellationToken);
        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return new ObsRuntimeLease(temporary);
        }
        catch
        {
            await ObsProcessController.StopAsync(temporary.ProcessId, CancellationToken.None);
            throw;
        }
    }

    private async Task<PreviewCapture?> CapturePreviewConnectedAsync(CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        var currentScene = await client.CallAsync("GetCurrentProgramScene", null, cancellationToken);
        var sceneName = currentScene.GetProperty("currentProgramSceneName").GetString();
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            return null;
        }

        var screenshot = await client.CallAsync(
            "GetSourceScreenshot",
            new
            {
                sourceName = sceneName,
                imageFormat = "webp",
                imageWidth = 960,
                imageCompressionQuality = 80
            },
            cancellationToken);
        var dataUri = screenshot.GetProperty("imageData").GetString();
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return null;
        }

        var separator = dataUri.IndexOf(',', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ObsRequestException("OBS 截图数据格式无效");
        }

        return new PreviewCapture(
            ApplicationKind.Obs,
            "image/webp",
            Convert.FromBase64String(dataUri[(separator + 1)..]),
            DateTimeOffset.UtcNow);
    }

    public async Task<RestorePreflightResult> PreflightAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        var original = ObsProcessController.FindRunning();
        var temporary = original is null ? await ObsProcessController.StartAsync(cancellationToken) : null;
        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            return await PreflightConnectedAsync(context, cancellationToken);
        }
        finally
        {
            if (temporary is not null)
            {
                await ObsProcessController.StopAsync(temporary.ProcessId, CancellationToken.None);
            }
        }
    }

    private async Task<RestorePreflightResult> PreflightConnectedAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
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
                "OBS 来源类型、属性 UI 与原生 inputSettings 的覆盖矩阵尚未双向闭合，禁止写入");
        }

        await using var client = await CreateClientAsync(cancellationToken);
        var inputs = await client.CallAsync("GetInputList", null, cancellationToken);
        var inputNames = inputs.GetProperty("inputs")
            .EnumerateArray()
            .Select(input => input.GetProperty("inputName").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var scenes = await client.CallAsync("GetSceneList", null, cancellationToken);
        var sceneNames = scenes.GetProperty("scenes")
            .EnumerateArray()
            .Select(scene => scene.GetProperty("sceneName").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var currentScene = await client.CallAsync("GetCurrentProgramScene", null, cancellationToken);
        var currentSceneName = currentScene.GetProperty("currentProgramSceneName").GetString() ?? string.Empty;
        var filterKindsResponse = await client.CallAsync("GetSourceFilterKindList", null, cancellationToken);
        var availableFilterKinds = filterKindsResponse.GetProperty("sourceFilterKinds")
            .EnumerateArray()
            .Select(kind => kind.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var mappings = CreateEffectiveMappings(context.Snapshot, context.Mappings, currentSceneName);
        foreach (var source in context.Snapshot.Sources)
        {
            if (!mappings.TryGetValue(source.LogicalId, out var mapping))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.MappingRequired,
                    $"来源 {source.Name} 尚未映射到目标电脑");
            }

            if (source.Mode is not null
                && !await deviceCatalog.SupportsModeAsync(
                    mapping.TargetDeviceId,
                    mapping.TargetSourceName,
                    source.Mode,
                    cancellationToken))
            {
                return RestorePreflightResult.Fail(
                    JobStatus.UnsupportedDeviceMode,
                    $"目标设备不支持 {source.Mode.Width}x{source.Mode.Height} {source.Mode.PixelFormat}");
            }

        }
        foreach (var source in context.Snapshot.Sources)
        {
            var mapping = mappings[source.LogicalId];
            if (!inputNames.Contains(mapping.TargetSourceName))
            {
                if (!mapping.CreateSourceWhenMissing || !sceneNames.Contains(mapping.TargetSceneName))
                {
                    return RestorePreflightResult.Fail(
                        JobStatus.MappingRequired,
                        $"目标来源 {mapping.TargetSourceName} 不存在，且没有可用的创建映射");
                }
            }

            var missingFilter = source.Filters.FirstOrDefault(filter => !availableFilterKinds.Contains(filter.Kind));
            if (missingFilter is not null)
            {
                return RestorePreflightResult.Fail(
                    JobStatus.MissingFilter,
                    $"OBS 缺少滤镜类型 {missingFilter.Kind}");
            }

            var missingAsset = source.Filters
                .SelectMany(filter => ObsFilterAssetMapper.FindUnresolvedAssetPaths(
                    filter.Settings,
                    filter.Assets,
                    assetPathResolver))
                .FirstOrDefault();
            if (missingAsset is not null)
            {
                return RestorePreflightResult.Fail(
                    JobStatus.MissingAsset,
                    $"OBS 滤镜素材不存在，且内置色卡中没有同名文件: {Path.GetFileName(missingAsset)}");
            }
        }

        return RestorePreflightResult.Success;
    }

    public async Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        var running = ObsProcessController.FindRunning();
        var startedHere = running is null ? await ObsProcessController.StartAsync(cancellationToken) : null;
        running ??= startedHere;
        var wasRunningBefore = context.ApplicationWasRunningBeforeRestore ?? startedHere is null;
        var temporary = wasRunningBefore ? null : running;
        try
        {
            await WaitUntilConnectedAsync(cancellationToken);
            var rollbackSnapshot = await CaptureAsync(cancellationToken);
            var existingInputNames = await GetInputNamesAsync(cancellationToken);
            var effectiveMappings = CreateEffectiveMappings(context.Snapshot, context.Mappings, string.Empty);
            var plannedCreatedSources = context.Snapshot.Sources
                .Select(source => effectiveMappings[source.LogicalId].TargetSourceName)
                .Where(name => !existingInputNames.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var journal = await ObsTransactionJournal.CreateAsync(
                context.JobId,
                rollbackSnapshot,
                wasRunningBefore,
                plannedCreatedSources,
                cancellationToken);
            return new ObsRestoreSession(this, context, rollbackSnapshot, temporary, journal);
        }
        catch
        {
            if (temporary is not null)
            {
                await ObsProcessController.StopAsync(temporary.ProcessId, CancellationToken.None);
            }

            throw;
        }
    }

    internal async Task ApplySnapshotAsync(
        ApplicationSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        string assetDirectory,
        List<string> createdSources,
        CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        var currentScene = await client.CallAsync("GetCurrentProgramScene", null, cancellationToken);
        var currentSceneName = currentScene.GetProperty("currentProgramSceneName").GetString() ?? string.Empty;
        var mappingBySource = CreateEffectiveMappings(snapshot, mappings, currentSceneName);
        var inputResponse = await client.CallAsync("GetInputList", null, cancellationToken);
        var existingInputs = inputResponse.GetProperty("inputs")
            .EnumerateArray()
            .Select(input => input.GetProperty("inputName").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var source in snapshot.Sources)
        {
            var mapping = mappingBySource[source.LogicalId];
            var targetSettings = ObsSnapshotMapper.CreateTargetSettings(source, mapping);
            if (!existingInputs.Contains(mapping.TargetSourceName))
            {
                await client.CallAsync(
                    "CreateInput",
                    new
                    {
                        sceneName = mapping.TargetSceneName,
                        inputName = mapping.TargetSourceName,
                        inputKind = source.Kind,
                        inputSettings = targetSettings,
                        sceneItemEnabled = true
                    },
                    cancellationToken);
                existingInputs.Add(mapping.TargetSourceName);
                createdSources.Add(mapping.TargetSourceName);
            }
            else
            {
                var currentSettingsResponse = await client.CallAsync(
                    "GetInputSettings",
                    new { inputName = mapping.TargetSourceName },
                    cancellationToken);
                var completeSettings = ObsSnapshotMapper.PreserveExcludedAudioSettings(
                    targetSettings,
                    currentSettingsResponse.GetProperty("inputSettings"));
                await client.CallAsync(
                    "SetInputSettings",
                    new
                    {
                        inputName = mapping.TargetSourceName,
                        inputSettings = completeSettings,
                        overlay = false
                    },
                    cancellationToken);
            }

            var currentFilters = await client.CallAsync(
                "GetSourceFilterList",
                new { sourceName = mapping.TargetSourceName },
                cancellationToken);
            foreach (var filter in currentFilters.GetProperty("filters").EnumerateArray())
            {
                var filterKind = filter.GetProperty("filterKind").GetString() ?? string.Empty;
                if (ObsConnectionOptions.BuiltInAudioFilterKinds.Contains(filterKind))
                {
                    continue;
                }

                await client.CallAsync(
                    "RemoveSourceFilter",
                    new
                    {
                        sourceName = mapping.TargetSourceName,
                        filterName = filter.GetProperty("filterName").GetString()
                    },
                    cancellationToken);
            }

            foreach (var filter in source.Filters.OrderBy(filter => filter.Order))
            {
                var settings = MaterializeFilterSettings(filter, assetDirectory);
                await client.CallAsync(
                    "CreateSourceFilter",
                    new
                    {
                        sourceName = mapping.TargetSourceName,
                        filterName = filter.Name,
                        filterKind = filter.Kind,
                        filterSettings = settings
                    },
                    cancellationToken);
                await client.CallAsync(
                    "SetSourceFilterEnabled",
                    new
                    {
                        sourceName = mapping.TargetSourceName,
                        filterName = filter.Name,
                        filterEnabled = filter.Enabled
                    },
                    cancellationToken);
                await client.CallAsync(
                    "SetSourceFilterIndex",
                    new
                    {
                        sourceName = mapping.TargetSourceName,
                        filterName = filter.Name,
                        filterIndex = filter.Order
                    },
                    cancellationToken);
            }
        }
    }

    internal async Task<RestoreVerificationResult> VerifyAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        var current = await CaptureAsync(cancellationToken);
        var currentByName = current.Sources.ToDictionary(source => source.Name, StringComparer.Ordinal);
        var mappings = CreateEffectiveMappings(context.Snapshot, context.Mappings, string.Empty);
        var differences = new List<string>();
        foreach (var expectedSource in context.Snapshot.Sources)
        {
            var mapping = mappings[expectedSource.LogicalId];
            if (!currentByName.TryGetValue(mapping.TargetSourceName, out var currentSource))
            {
                differences.Add($"找不到来源 {mapping.TargetSourceName}");
                continue;
            }

            var expectedSettings = ObsSnapshotMapper.CreateTargetSettings(expectedSource, mapping);
            if (!SettingsEqual(expectedSettings, currentSource.Settings))
            {
                differences.Add($"{mapping.TargetSourceName}.inputSettings 不一致");
            }

            var expectedFilters = expectedSource.Filters.OrderBy(filter => filter.Order).ToArray();
            var actualFilters = currentSource.Filters.OrderBy(filter => filter.Order).ToArray();
            if (expectedFilters.Length != actualFilters.Length)
            {
                differences.Add($"{mapping.TargetSourceName} 滤镜数量不一致");
                continue;
            }

            for (var index = 0; index < expectedFilters.Length; index++)
            {
                var expected = expectedFilters[index];
                var actual = actualFilters[index];
                if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
                    || !string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)
                    || expected.Enabled != actual.Enabled
                    || expected.Order != actual.Order)
                {
                    differences.Add($"{mapping.TargetSourceName} 滤镜链第 {index + 1} 项不一致");
                    continue;
                }

                var expectedFilterSettings = MaterializeFilterSettings(expected, context.AssetDirectory);
                if (!SettingsEqual(expectedFilterSettings, actual.Settings))
                {
                    differences.Add($"{mapping.TargetSourceName}.{expected.Name} 参数不一致");
                }
            }
        }

        return new RestoreVerificationResult(differences.Count == 0, differences);
    }

    internal static Dictionary<Guid, DeviceMapping> CreateEffectiveMappings(
        ApplicationSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        string currentSceneName)
    {
        var result = mappings.ToDictionary(mapping => mapping.SourceLogicalId);
        foreach (var source in snapshot.Sources)
        {
            if (result.TryGetValue(source.LogicalId, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.TargetSceneName)
                    && !string.IsNullOrWhiteSpace(currentSceneName))
                {
                    result[source.LogicalId] = existing with
                    {
                        TargetSceneName = currentSceneName,
                        CreateSourceWhenMissing = true
                    };
                }

                continue;
            }

            var targetDeviceId = source.Settings.TryGetValue("video_device_id", out var deviceId)
                                 && deviceId.ValueKind == JsonValueKind.String
                ? deviceId.GetString() ?? string.Empty
                : string.Empty;
            result[source.LogicalId] = new DeviceMapping(
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
                source.LogicalId,
                ApplicationKind.Obs,
                targetDeviceId,
                source.Name,
                currentSceneName,
                !string.IsNullOrWhiteSpace(currentSceneName));
        }

        return result;
    }

    internal async Task RemoveInputsAsync(
        IEnumerable<string> inputNames,
        CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        var existing = (await client.CallAsync("GetInputList", null, cancellationToken))
            .GetProperty("inputs")
            .EnumerateArray()
            .Select(input => input.GetProperty("inputName").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var inputName in inputNames.Where(existing.Contains))
        {
            await client.CallAsync("RemoveInput", new { inputName }, cancellationToken);
        }
    }

    internal async Task<HashSet<string>> GetInputNamesAsync(CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        return (await client.CallAsync("GetInputList", null, cancellationToken))
            .GetProperty("inputs")
            .EnumerateArray()
            .Select(input => input.GetProperty("inputName").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<ObsWebSocketClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var password = await credentialProvider.GetPasswordAsync(cancellationToken);
        var client = new ObsWebSocketClient(optionsProvider.Current.Endpoint, password);
        try
        {
            await client.ConnectAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task WaitUntilConnectedAsync(CancellationToken cancellationToken)
    {
        await WaitUntilConnectedAsync(
            async token =>
            {
                if (ObsProcessController.FindRunning() is { } running)
                {
                    _ = ObsProcessController.TryDismissUncleanShutdownDialog(running.ProcessId);
                }

                await using var client = await CreateClientAsync(token);
            },
            TimeSpan.FromSeconds(35),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    internal static async Task WaitUntilConnectedAsync(
        Func<CancellationToken, Task> connect,
        TimeSpan readyTimeout,
        TimeSpan attemptTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readyTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(attemptTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        var deadline = DateTimeOffset.UtcNow + readyTimeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = deadline - DateTimeOffset.UtcNow;
            attemptCancellation.CancelAfter(remaining < attemptTimeout ? remaining : attemptTimeout);
            try
            {
                await connect(attemptCancellation.Token);
                return;
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                lastError = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException("OBS WebSocket 单次连接超时", exception);
            }

            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < retryDelay ? remaining : retryDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            $"OBS 已启动，但 obs-websocket 在 {readyTimeout.TotalSeconds:0.#} 秒内没有就绪",
            lastError);
    }

    internal Task WaitUntilConnectedForRecoveryAsync(CancellationToken cancellationToken) =>
        WaitUntilConnectedAsync(cancellationToken);

    internal static bool IsConnectionFailure(Exception exception) =>
        exception is WebSocketException or ObsRequestException or HttpRequestException;

    private Dictionary<string, JsonElement> MaterializeFilterSettings(
        VideoFilter filter,
        string assetDirectory)
    {
        return ObsFilterAssetMapper.Materialize(
            filter.Settings,
            filter.Assets,
            assetDirectory,
            assetPathResolver);
    }

    private static bool SettingsEqual(
        Dictionary<string, JsonElement> expected,
        IReadOnlyDictionary<string, JsonElement> actual) =>
        expected.Count == actual.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
            && JsonElement.DeepEquals(pair.Value, value));

    private sealed class ObsRestoreSession(
        ObsAdapter adapter,
        RestoreExecutionContext context,
        ApplicationSnapshot rollbackSnapshot,
        ObsProcessInfo? temporaryProcess,
        ObsTransactionJournal journal) : IApplicationRestoreSession
    {
        private readonly List<string> _createdSources = [];

        public ApplicationKind Kind => ApplicationKind.Obs;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ApplyAsync(CancellationToken cancellationToken) => adapter.ApplySnapshotAsync(
            context.Snapshot,
            context.Mappings,
            context.AssetDirectory,
            _createdSources,
            cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RestoreVerificationResult> VerifyAsync(CancellationToken cancellationToken) =>
            adapter.VerifyAsync(context, cancellationToken);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (temporaryProcess is not null)
            {
                await ObsProcessController.StopAsync(temporaryProcess.ProcessId, cancellationToken);
            }

            // 协调器可能在另一应用提交时失败；回滚快照一直保留到 DisposeAsync，
            // 不能仅因本应用先完成提交就拒绝全局回滚。
        }

        public Task CompleteAsync(CancellationToken cancellationToken) =>
            journal.CompleteAsync(cancellationToken);

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            await adapter.RemoveInputsAsync(
                _createdSources.Concat(journal.CreatedSourceNames).Distinct(StringComparer.Ordinal),
                cancellationToken);
            var rollbackMappings = ObsTransactionJournal.CreateRollbackMappings(rollbackSnapshot);
            var unusedCreatedSources = new List<string>();
            await adapter.ApplySnapshotAsync(
                rollbackSnapshot,
                rollbackMappings,
                journal.AssetDirectory,
                unusedCreatedSources,
                cancellationToken);
            if (temporaryProcess is not null)
            {
                await ObsProcessController.StopAsync(temporaryProcess.ProcessId, cancellationToken);
            }

            await journal.CompleteAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObsRuntimeLease(ObsProcessInfo temporaryProcess) : IApplicationRuntimeLease
    {
        public bool WasRunning => false;

        public async ValueTask DisposeAsync()
        {
            var running = ObsProcessController.FindRunning();
            if (running?.ProcessId == temporaryProcess.ProcessId)
            {
                await ObsProcessController.StopAsync(temporaryProcess.ProcessId, CancellationToken.None);
            }
        }
    }
}
