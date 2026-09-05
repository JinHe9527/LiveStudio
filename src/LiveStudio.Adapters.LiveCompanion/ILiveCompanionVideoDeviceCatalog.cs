using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public interface ILiveCompanionVideoDeviceCatalog
{
    Task<RestorePreflightResult> ValidateAsync(string deviceId, VideoMode mode, CancellationToken cancellationToken);
}
