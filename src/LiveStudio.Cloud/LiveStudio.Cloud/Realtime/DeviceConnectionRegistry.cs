using System.Collections.Concurrent;

namespace LiveStudio.Cloud.Realtime;

public sealed class DeviceConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public void Connected(Guid deviceId) => _connections.AddOrUpdate(deviceId, 1, (_, count) => count + 1);

    public void Disconnected(Guid deviceId)
    {
        while (_connections.TryGetValue(deviceId, out var count))
        {
            if (count <= 1)
            {
                if (_connections.TryRemove(new KeyValuePair<Guid, int>(deviceId, count)))
                {
                    return;
                }
            }
            else if (_connections.TryUpdate(deviceId, count - 1, count))
            {
                return;
            }
        }
    }

    public bool IsConnected(Guid deviceId) => _connections.ContainsKey(deviceId);
}
