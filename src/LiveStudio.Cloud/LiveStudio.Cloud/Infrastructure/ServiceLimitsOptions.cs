namespace LiveStudio.Cloud.Infrastructure;

public sealed class ServiceLimitsOptions
{
    public const string SectionName = "ServiceLimits";

    public int MaximumManagedDevices { get; init; } = 15;

    public int MaximumLiveRooms { get; init; } = 15;
}
