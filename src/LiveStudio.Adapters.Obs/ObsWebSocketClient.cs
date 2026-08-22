using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Adapters.Obs;

public sealed class ObsWebSocketClient(Uri endpoint, string password) : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _isConnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(endpoint, cancellationToken);
        using var hello = await ReceiveAsync(cancellationToken);
        if (hello.RootElement.GetProperty("op").GetInt32() != 0)
        {
            throw new ObsRequestException("OBS WebSocket 未返回 Hello 消息");
        }

        var identifyData = new Dictionary<string, object?>
        {
            ["rpcVersion"] = 1,
            ["eventSubscriptions"] = 0
        };
        var helloData = hello.RootElement.GetProperty("d");
        if (helloData.TryGetProperty("authentication", out var authentication))
        {
            identifyData["authentication"] = CreateAuthentication(
                password,
                authentication.GetProperty("salt").GetString() ?? string.Empty,
                authentication.GetProperty("challenge").GetString() ?? string.Empty);
        }

        await SendAsync(new { op = 1, d = identifyData }, cancellationToken);
        using var identified = await ReceiveAsync(cancellationToken);
        if (identified.RootElement.GetProperty("op").GetInt32() != 2)
        {
            throw new ObsRequestException("OBS WebSocket 身份验证失败");
        }

        _isConnected = true;
    }

    public async Task<JsonElement> CallAsync(
        string requestType,
        object? requestData,
        CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("OBS WebSocket 尚未连接");
        }

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            await SendAsync(
                new
                {
                    op = 6,
                    d = new
                    {
                        requestType,
                        requestId,
                        requestData = requestData ?? new { }
                    }
                },
                cancellationToken);

            while (true)
            {
                using var response = await ReceiveAsync(cancellationToken);
                if (response.RootElement.GetProperty("op").GetInt32() != 7)
                {
                    continue;
                }

                var data = response.RootElement.GetProperty("d");
                if (!string.Equals(data.GetProperty("requestId").GetString(), requestId, StringComparison.Ordinal))
                {
                    continue;
                }

                var status = data.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                {
                    var code = status.GetProperty("code").GetInt32();
                    var comment = status.TryGetProperty("comment", out var commentElement)
                        ? commentElement.GetString()
                        : null;
                    throw new ObsRequestException($"{requestType} 失败 ({code}): {comment}");
                }

                return data.TryGetProperty("responseData", out var responseData)
                    ? responseData.Clone()
                    : JsonSerializer.SerializeToElement(new { });
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
        _requestLock.Dispose();
    }

    private async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message);
        await _socket.SendAsync(content, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new ObsRequestException("OBS WebSocket 已关闭连接");
            }

            await output.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(output.ToArray());
    }

    private static string CreateAuthentication(string passwordValue, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(passwordValue + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
}
