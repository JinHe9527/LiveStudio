using LiveStudio.Contracts;

namespace LiveStudio.Adapters.Obs;

public sealed record ObsConnectionOptions(Uri Endpoint, IReadOnlySet<string> VideoFilterKinds)
{
    public static IReadOnlySet<string> BuiltInVideoFilterKinds { get; } = new HashSet<string>(
        [
            "async_delay_filter",
            "chroma_key_filter",
            "chroma_key_filter_v2",
            "color_filter",
            "color_filter_v2",
            "color_grade_filter",
            "clut_filter",
            "color_key_filter",
            "color_key_filter_v2",
            "crop_filter",
            "gpu_delay",
            "hdr_tonemap_filter",
            "sdr_on_hdr_filter",
            "luma_key_filter",
            "luma_key_filter_v2",
            "mask_filter",
            "mask_filter_v2",
            "render_delay_filter",
            "scale_filter",
            "scroll_filter",
            "sharpness_filter",
            "sharpness_filter_v2"
        ],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> BuiltInAudioFilterKinds { get; } = new HashSet<string>(
        [
            "compressor_filter",
            "eq_filter",
            "expander_filter",
            "gain_filter",
            "invert_polarity_filter",
            "limiter_filter",
            "noise_gate_filter",
            "noise_suppress_filter",
            "noise_suppress_filter_v2",
            "upward_compressor_filter",
            "vst_filter"
        ],
        StringComparer.Ordinal);
}

public interface IObsCredentialProvider
{
    ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken);
}

public interface IObsConnectionOptionsProvider
{
    ObsConnectionOptions Current { get; }
}

public interface IObsDeviceCatalog
{
    Task<bool> SupportsModeAsync(
        string targetDeviceId,
        string targetSourceName,
        VideoMode mode,
        CancellationToken cancellationToken);
}

public sealed class ObsRequestException : Exception
{
    public ObsRequestException()
    {
    }

    public ObsRequestException(string message)
        : base(message)
    {
    }

    public ObsRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
