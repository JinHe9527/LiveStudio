using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;

namespace LiveStudio.Agent;

public sealed class CurrentStatePublisher(
    IEnumerable<IApplicationAdapter> applicationAdapters,
    DeviceApiClient apiClient)
{
    private readonly IReadOnlyList<IApplicationAdapter> adapters = applicationAdapters.ToArray();

    public async Task PublishAsync(
        CurrentStateReason reason,
        CancellationToken cancellationToken)
    {
        if (reason == CurrentStateReason.Heartbeat)
        {
            throw new ArgumentException("显式发布当前状态必须标明手动刷新或恢复", nameof(reason));
        }

        var applications = new List<ApplicationSnapshot>();
        var previews = new List<CurrentPreviewUpload>();
        foreach (var adapter in adapters)
        {
            var status = await adapter.InspectAsync(cancellationToken);
            if (!status.IsRunning)
            {
                continue;
            }

            applications.Add(await adapter.CaptureAsync(cancellationToken));
            if (await adapter.CapturePreviewAsync(cancellationToken) is { } preview)
            {
                previews.Add(new CurrentPreviewUpload(
                    preview.Application,
                    preview.MediaType,
                    Convert.ToBase64String(preview.Content.Span)));
            }
        }

        if (applications.Count == 0)
        {
            throw new InvalidOperationException("没有可上报的当前应用参数");
        }

        var ordered = applications.OrderBy(application => application.Kind).ToArray();
        var content = JsonSerializer.SerializeToUtf8Bytes(ordered);
        var state = new CurrentParameterState(
            apiClient.Credentials.DeviceId,
            apiClient.Credentials.RoomId,
            DateTimeOffset.UtcNow,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            ordered);
        await apiClient.UpdateCurrentStateAsync(state, previews, reason, cancellationToken);
    }
}
