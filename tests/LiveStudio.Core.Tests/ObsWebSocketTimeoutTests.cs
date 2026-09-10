using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using LiveStudio.Adapters.Obs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Core.Tests;

public sealed class ObsWebSocketTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentHelloOrRequestFailsWithinDeadline(bool completeHandshake)
    {
        await using var server = await SilentServer.StartAsync(completeHandshake);
        await using var client = new ObsWebSocketClient(server.Endpoint, "")
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(200)
        };
        var timer = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ObsRequestException>(async () =>
        {
            await client.ConnectAsync(CancellationToken.None);
            if (completeHandshake)
            {
                await client.CallAsync("GetVersion", null, CancellationToken.None);
            }
        });
        Assert.Contains("响应超时", exception.Message, StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task UnansweredCloseDoesNotHangDisposal()
    {
        await using var server = await SilentServer.StartAsync(true);
        var client = new ObsWebSocketClient(server.Endpoint, "")
        {
            CloseTimeout = TimeSpan.FromMilliseconds(100)
        };
        await client.ConnectAsync(CancellationToken.None);
        var timer = Stopwatch.StartNew();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        await using var server = await SilentServer.StartAsync(false);
        await using var client = new ObsWebSocketClient(server.Endpoint, "");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancellation.Token));
    }

    private sealed class SilentServer(WebApplication application, TaskCompletionSource release) : IAsyncDisposable
    {
        public Uri Endpoint => new(application.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal));

        public static async Task<SilentServer> StartAsync(bool completeHandshake)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.UseWebSockets();
            app.Run(async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                if (completeHandshake)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"op\":0,\"d\":{\"rpcVersion\":1}}"),
                        WebSocketMessageType.Text, true, context.RequestAborted);
                    await socket.ReceiveAsync(new byte[4096], context.RequestAborted);
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}"),
                        WebSocketMessageType.Text, true, context.RequestAborted);
                }

                await release.Task;
            });
            await app.StartAsync();
            return new SilentServer(app, release);
        }

        public async ValueTask DisposeAsync()
        {
            release.TrySetResult();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
