using System.Text.Json;
using LiveStudio.Adapters.Obs;
using LiveStudio.Contracts;

namespace LiveStudio.Agent;

public sealed class AgentObsDeviceCatalog(
    IObsConnectionOptionsProvider optionsProvider,
    IObsCredentialProvider credentialProvider) : IObsDeviceCatalog
{
    private const long DirectShowTicksPerSecond = 10_000_000;

    public async Task<bool> SupportsModeAsync(
        string targetDeviceId,
        string targetSourceName,
        VideoMode mode,
        CancellationToken cancellationToken)
    {
        var password = await credentialProvider.GetPasswordAsync(cancellationToken);
        await using var client = new ObsWebSocketClient(optionsProvider.Current.Endpoint, password);
        await client.ConnectAsync(cancellationToken);
        var sourceName = await FindCapabilitySourceAsync(
            client,
            targetSourceName,
            targetDeviceId,
            cancellationToken);
        if (sourceName is null)
        {
            return false;
        }

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
            && denominator.TryGetInt32(out var actualDenominator))
        {
            if ((long)actualNumerator * mode.FramesPerSecondDenominator
                == (long)mode.FramesPerSecondNumerator * actualDenominator)
            {
                return true;
            }
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
