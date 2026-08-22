using System.Net;
using System.Text;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task CheckAsyncReturnsNewerPrivateReleaseAndUsesBearerToken()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new RouteHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {
                  "tag_name": "v0.2.0",
                  "name": "LiveStudio v0.2.0",
                  "published_at": "2026-08-20T08:00:00Z",
                  "assets": [
                    { "name": "LiveStudio-Windows-x64.zip", "url": "https://api.github.com/assets/1" },
                    { "name": "LiveStudio-Windows-x64.zip.sha256", "url": "https://api.github.com/assets/2" }
                  ]
                }
                """);
        });
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var release = await service.CheckAsync("private-token", CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("https://api.github.com/repos/owner/repository/releases/latest", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("private-token", capturedRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CheckAsyncReturnsNullWhenReleaseIsCurrentVersion()
    {
        var handler = new RouteHandler(_ => JsonResponse("""
            {
              "tag_name": "v0.1.0",
              "name": "LiveStudio v0.1.0",
              "published_at": "2026-08-20T08:00:00Z",
              "assets": []
            }
            """));
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var release = await service.CheckAsync("private-token", CancellationToken.None);

        Assert.Null(release);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
