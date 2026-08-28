using System.Text.Json;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class AgentObsDeviceCatalog(
    IObsConnectionOptionsProvider optionsProvider,
    IObsCredentialProvider credentialProvider) : IObsDeviceCatalog
{
    private const long DirectShowTicksPerSecond = 10_000_000;
    private const string ProbeInputPrefix = "__LiveStudio_DeviceProbe_";

    public async Task<IReadOnlyList<ObsVideoDevice>> ListVideoDevicesAsync(
        CancellationToken cancellationToken)
    {
        var password = await credentialProvider.GetPasswordAsync(cancellationToken);
        await using var client = new ObsWebSocketClient(optionsProvider.Current.Endpoint, password);
        await client.ConnectAsync(cancellationToken);
        await RemoveStaleProbeInputsAsync(client, cancellationToken);

        var sourceName = await FindAnyDirectShowSourceAsync(client, cancellationToken);
        var createdProbe = sourceName is null;
        sourceName ??= await CreateProbeSourceAsync(client, null, cancellationToken);
        try
        {
            var response = await client.CallAsync(
                "GetInputPropertiesListPropertyItems",
                new { inputName = sourceName, propertyName = "video_device_id" },
                cancellationToken);
            return ParseVideoDevices(response.GetProperty("propertyItems"));
        }
        finally
        {
            if (createdProbe)
            {
                await RemoveInputIgnoringMissingAsync(client, sourceName, CancellationToken.None);
            }
        }
    }

    public async Task<bool> SupportsModeAsync(
        string targetDeviceId,
        string targetSourceName,
        VideoMode mode,
        CancellationToken cancellationToken)
    {
        var password = await credentialProvider.GetPasswordAsync(cancellationToken);
        await using var client = new ObsWebSocketClient(optionsProvider.Current.Endpoint, password);
        await client.ConnectAsync(cancellationToken);
        await RemoveStaleProbeInputsAsync(client, cancellationToken);
        var sourceName = await FindCapabilitySourceAsync(
            client,
            targetSourceName,
            targetDeviceId,
            cancellationToken);
        var createdProbe = sourceName is null;
        sourceName ??= await CreateProbeSourceAsync(client, targetDeviceId, cancellationToken);
        try
        {
            var response = await client.CallAsync(
                "GetInputSettings",
                new { inputName = sourceName },
                cancellationToken);

            var settings = response.GetProperty("inputSettings");
            if (!Matches(settings, "video_device_id", targetDeviceId))
            {
                return false;
            }

            if (!await SupportsValueAsync(
                    client,
                    sourceName,
                    settings,
                    "resolution",
                    $"{mode.Width}x{mode.Height}",
                    cancellationToken)
                || !await SupportsRequiredValueAsync(
                    client,
                    sourceName,
                    settings,
                    "video_format",
                    mode.PixelFormat,
                    cancellationToken)
                || !await SupportsRequiredValueAsync(
                    client,
                    sourceName,
                    settings,
                    "color_space",
                    mode.ColorSpace,
                    cancellationToken)
                || !await SupportsRequiredValueAsync(
                    client,
                    sourceName,
                    settings,
                    "color_range",
                    mode.ColorRange,
                    cancellationToken))
            {
                return false;
            }

            if (settings.TryGetProperty("fps_num", out var numerator)
                && settings.TryGetProperty("fps_den", out var denominator)
                && numerator.TryGetInt32(out var actualNumerator)
                && denominator.TryGetInt32(out var actualDenominator)
                && (long)actualNumerator * mode.FramesPerSecondDenominator
                == (long)mode.FramesPerSecondNumerator * actualDenominator)
            {
                return true;
            }

            if (mode.FramesPerSecondNumerator <= 0)
            {
                return false;
            }

            var expectedInterval = checked(
                DirectShowTicksPerSecond * mode.FramesPerSecondDenominator / mode.FramesPerSecondNumerator);
            if (settings.TryGetProperty("frame_interval", out var interval)
                && interval.TryGetInt64(out var actualInterval)
                && actualInterval == expectedInterval)
            {
                return true;
            }

            return await ContainsPropertyValueAsync(
                client,
                sourceName,
                "frame_interval",
                expectedInterval.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
        }
        finally
        {
            if (createdProbe)
            {
                await RemoveInputIgnoringMissingAsync(client, sourceName, CancellationToken.None);
            }
        }
    }

    internal static IReadOnlyList<ObsVideoDevice> ParseVideoDevices(JsonElement propertyItems) =>
        propertyItems.EnumerateArray()
            .Where(item => !item.TryGetProperty("itemEnabled", out var enabled)
                           || enabled.GetBoolean())
            .Select(item => new ObsVideoDevice(
                item.GetProperty("itemValue").ToString(),
                item.GetProperty("itemName").GetString() ?? item.GetProperty("itemValue").ToString()))
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId))
            .DistinctBy(device => device.DeviceId, StringComparer.Ordinal)
            .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task<string?> FindAnyDirectShowSourceAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken)
    {
        var inputs = await client.CallAsync("GetInputList", null, cancellationToken);
        return inputs.GetProperty("inputs").EnumerateArray()
            .Where(input => string.Equals(
                input.GetProperty("inputKind").GetString(),
                "dshow_input",
                StringComparison.Ordinal))
            .Select(input => input.GetProperty("inputName").GetString())
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<string> CreateProbeSourceAsync(
        ObsWebSocketClient client,
        string? targetDeviceId,
        CancellationToken cancellationToken)
    {
        var scene = await client.CallAsync("GetCurrentProgramScene", null, cancellationToken);
        var sceneName = scene.GetProperty("currentProgramSceneName").GetString();
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            throw new InvalidOperationException("OBS 当前没有可用于设备探测的场景");
        }

        var sourceName = ProbeInputPrefix + Guid.NewGuid().ToString("N");
        object settings = string.IsNullOrWhiteSpace(targetDeviceId)
            ? new { }
            : new { video_device_id = targetDeviceId };
        await client.CallAsync(
            "CreateInput",
            new
            {
                sceneName,
                inputName = sourceName,
                inputKind = "dshow_input",
                inputSettings = settings,
                sceneItemEnabled = false
            },
            cancellationToken);
        return sourceName;
    }

    private static async Task RemoveStaleProbeInputsAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken)
    {
        var inputs = await client.CallAsync("GetInputList", null, cancellationToken);
        foreach (var name in inputs.GetProperty("inputs").EnumerateArray()
                     .Select(input => input.GetProperty("inputName").GetString())
                     .Where(name => name?.StartsWith(ProbeInputPrefix, StringComparison.Ordinal) == true))
        {
            await RemoveInputIgnoringMissingAsync(client, name!, cancellationToken);
        }
    }

    private static async Task RemoveInputIgnoringMissingAsync(
        ObsWebSocketClient client,
        string inputName,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.CallAsync("RemoveInput", new { inputName }, cancellationToken);
        }
        catch (ObsRequestException exception) when (exception.StatusCode is 600 or 601)
        {
        }
    }

    private static async Task<string?> FindCapabilitySourceAsync(
        ObsWebSocketClient client,
        string preferredSourceName,
        string targetDeviceId,
        CancellationToken cancellationToken)
    {
        var inputs = await client.CallAsync("GetInputList", null, cancellationToken);
        var names = inputs.GetProperty("inputs").EnumerateArray()
            .Select(input => input.GetProperty("inputName").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderByDescending(name => string.Equals(name, preferredSourceName, StringComparison.Ordinal))
            .ToArray();
        foreach (var name in names)
        {
            try
            {
                var response = await client.CallAsync(
                    "GetInputSettings",
                    new { inputName = name },
                    cancellationToken);
                if (Matches(response.GetProperty("inputSettings"), "video_device_id", targetDeviceId))
                {
                    return name;
                }
            }
            catch (ObsRequestException)
            {
            }
        }

        return null;
    }

    private static bool Matches(JsonElement settings, string propertyName, string expected) =>
        settings.TryGetProperty(propertyName, out var actual)
        && string.Equals(actual.ToString(), expected, StringComparison.Ordinal);

    private static async Task<bool> SupportsRequiredValueAsync(
        ObsWebSocketClient client,
        string sourceName,
        JsonElement settings,
        string propertyName,
        string expected,
        CancellationToken cancellationToken) => !string.IsNullOrWhiteSpace(expected)
        && await SupportsValueAsync(
            client,
            sourceName,
            settings,
            propertyName,
            expected,
            cancellationToken);

    private static async Task<bool> SupportsValueAsync(
        ObsWebSocketClient client,
        string sourceName,
        JsonElement settings,
        string propertyName,
        string expected,
        CancellationToken cancellationToken) => Matches(settings, propertyName, expected)
        || await ContainsPropertyValueAsync(
            client,
            sourceName,
            propertyName,
            expected,
            cancellationToken);

    private static async Task<bool> ContainsPropertyValueAsync(
        ObsWebSocketClient client,
        string sourceName,
        string propertyName,
        string expected,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.CallAsync(
                "GetInputPropertiesListPropertyItems",
                new { inputName = sourceName, propertyName },
                cancellationToken);
            return response.GetProperty("propertyItems")
                .EnumerateArray()
                .Any(item => item.GetProperty("itemEnabled").GetBoolean()
                    && string.Equals(
                        item.GetProperty("itemValue").ToString(),
                        expected,
                        StringComparison.Ordinal));
        }
        catch (ObsRequestException)
        {
            return false;
        }
    }
}
