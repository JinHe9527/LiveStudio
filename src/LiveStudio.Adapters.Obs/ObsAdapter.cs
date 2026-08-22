using System.Net.WebSockets;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.Obs;

public sealed class ObsAdapter(
    IObsConnectionOptionsProvider optionsProvider,
    IObsCredentialProvider credentialProvider,
    IObsDeviceCatalog deviceCatalog) : IApplicationAdapter
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
        catch (Exception exception) when (exception is WebSocketException or ObsRequestException)
        {
            return new ApplicationRuntimeStatus(false, false, false, "unknown", false);
        }
    }

    public async Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        return await ObsSnapshotMapper.CaptureAsync(
            client,
            cancellationToken);
    }

    public Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken) =>
        CaptureAsync(cancellationToken);

    public async Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken)
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
        var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
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
        var filterKindsResponse = await client.CallAsync("GetSourceFilterKindList", null, cancellationToken);
        var availableFilterKinds = filterKindsResponse.GetProperty("sourceFilterKinds")
            .EnumerateArray()
            .Select(kind => kind.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

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
        }

        return RestorePreflightResult.Success;
    }

    public async Task<IApplicationRestoreSession> BeginRestoreAsync(
        RestoreExecutionContext context,
        CancellationToken cancellationToken)
    {
        var rollbackSnapshot = await CaptureAsync(cancellationToken);
        return new ObsRestoreSession(this, context, rollbackSnapshot);
    }

    internal async Task ApplySnapshotAsync(
        ApplicationSnapshot snapshot,
        IReadOnlyList<DeviceMapping> mappings,
        string assetDirectory,
        List<string> createdSources,
        CancellationToken cancellationToken)
    {
        var mappingBySource = mappings.ToDictionary(mapping => mapping.SourceLogicalId);
        await using var client = await CreateClientAsync(cancellationToken);
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
                await client.CallAsync(
                    "SetInputSettings",
                    new
                    {
                        inputName = mapping.TargetSourceName,
                        inputSettings = targetSettings,
                        overlay = true
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
        var mappings = context.Mappings.ToDictionary(mapping => mapping.SourceLogicalId);
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
            foreach (var expected in expectedSettings)
            {
                if (!currentSource.Settings.TryGetValue(expected.Key, out var actual)
                    || !JsonElement.DeepEquals(expected.Value, actual))
                {
                    differences.Add($"{mapping.TargetSourceName}.{expected.Key} 不一致");
                }
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

    internal async Task RemoveInputsAsync(
        IEnumerable<string> inputNames,
        CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        foreach (var inputName in inputNames)
        {
            await client.CallAsync("RemoveInput", new { inputName }, cancellationToken);
        }
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

    private static Dictionary<string, JsonElement> MaterializeFilterSettings(
        VideoFilter filter,
        string assetDirectory)
    {
        return ObsFilterAssetMapper.Materialize(filter.Settings, filter.Assets, assetDirectory);
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
        ApplicationSnapshot rollbackSnapshot) : IApplicationRestoreSession
    {
        private readonly List<string> _createdSources = [];
        private bool _committed;

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

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _committed = true;
            return Task.CompletedTask;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (_committed)
            {
                return;
            }

            await adapter.RemoveInputsAsync(_createdSources, cancellationToken);
            var rollbackMappings = rollbackSnapshot.Sources.Select(source => new DeviceMapping(
                Guid.NewGuid(),
                Guid.Empty,
                Guid.Empty,
                source.LogicalId,
                ApplicationKind.Obs,
                source.Settings.TryGetValue("video_device_id", out var deviceId) ? deviceId.GetString() ?? string.Empty : string.Empty,
                source.Name,
                string.Empty,
                false)).ToArray();
            var unusedCreatedSources = new List<string>();
            await adapter.ApplySnapshotAsync(
                rollbackSnapshot,
                rollbackMappings,
                context.AssetDirectory,
                unusedCreatedSources,
                cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
