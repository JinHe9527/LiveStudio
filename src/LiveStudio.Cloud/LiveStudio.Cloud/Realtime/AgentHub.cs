using System.Security.Claims;
using LiveStudio.Cloud.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LiveStudio.Cloud.Realtime;

[Authorize(AuthenticationSchemes = DeviceAuthenticationHandler.AuthenticationScheme)]
public sealed class AgentHub(DeviceConnectionRegistry registry) : Hub
{
    public static string DeviceGroup(Guid organizationId, Guid deviceId) =>
        $"organization:{organizationId:N}:device:{deviceId:N}";

    public override async Task OnConnectedAsync()
    {
        var deviceId = GetDeviceId();
        var organizationId = GetOrganizationId();
        registry.Connected(deviceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(organizationId, deviceId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetDeviceId(out var deviceId))
        {
            registry.Disconnected(deviceId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Guid GetDeviceId() => TryGetDeviceId(out var deviceId)
        ? deviceId
        : throw new HubException("设备身份缺失");

    private Guid GetOrganizationId() => Guid.TryParse(
        Context.User?.FindFirstValue(DeviceAuthenticationHandler.OrganizationIdClaim),
        out var organizationId)
        ? organizationId
        : throw new HubException("Organization 身份缺失");

    private bool TryGetDeviceId(out Guid deviceId) => Guid.TryParse(
        Context.User?.FindFirstValue(DeviceAuthenticationHandler.DeviceIdClaim),
        out deviceId);
}
