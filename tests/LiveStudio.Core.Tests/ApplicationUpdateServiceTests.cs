using System.Net;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Theory]
    [InlineData("https://wuyoupaiban.cn/livestudio/latest.json")]
    [InlineData("https://111.229.162.72/livestudio/latest.json")]
    public async Task CheckAsyncNeverContactsRetiredTencentMirror(string retiredUrl)
    {
        var requests = new List<Uri>();
        var service = new ApplicationUpdateService(new RouteHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Redirect("https://github.com/owner/repository/releases/tag/v0.2.0");
        }), "owner", "repository", new Version(0, 2, 0), domesticManifestUri: new Uri(retiredUrl));

        Assert.Null(await service.CheckAsync(CancellationToken.None));
        Assert.Single(requests);
        Assert.Equal("github.com", requests[0].Host);
    }

    [Fact]
    public async Task CheckAsyncIdentifiesARealLocalProxyWithNoListener()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = new ApplicationUpdateService(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(timeout.Token));

        Assert.Contains("连接被拒绝", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{port}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsyncExplainsRefusedConnectionAndKeepsCause()
    {
        var cause = new HttpRequestException("连接失败 (127.0.0.1:7890)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        var service = new ApplicationUpdateService(new RouteHandler(_ => throw cause));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(CancellationToken.None));

        Assert.Contains("https://github.com", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Windows 代理", exception.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:7890", exception.Message, StringComparison.Ordinal);
        Assert.Same(cause, exception.InnerException);
    }

    [Fact]
    public async Task CheckAsyncReportsTimeoutAsRecoverableFailure()
    {
        var service = new ApplicationUpdateService(new RouteHandler(_ => throw new TaskCanceledException()));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(CancellationToken.None));
        Assert.Contains("超时", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsyncPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ApplicationUpdateService(new RouteHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/package", false)]
    [InlineData("https://untrusted.example/package", true)]
    public async Task CheckAsyncRejectsBrokenOrUntrustedAssetRedirect(string target, bool untrusted)
    {
        var handler = new RouteHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/owner/repository/releases/latest" => Redirect("https://github.com/owner/repository/releases/tag/v0.2.0"),
            "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe" => Redirect(target),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));
        if (untrusted)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task CheckAsyncPrefersNewerDomesticRelease()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var manifestUri = new Uri("https://download.example.cn/livestudio/latest.json");
        var handler = new RouteHandler(request =>
        {
            capturedRequests.Add(request);
            return request.RequestUri?.AbsolutePath switch
            {
                "/livestudio/latest.json" => JsonResponse(new
                {
                    version = "0.2.0",
                    tagName = "v0.2.0",
                    name = "LiveStudio v0.2.0",
                    publishedAt = "2026-09-04T18:00:00+08:00",
                    packageUrl = "https://download.example.cn/livestudio/LiveStudio-Setup.exe?v=0.2.0",
                    checksumUrl = "https://download.example.cn/livestudio/LiveStudio-Setup.exe.sha256?v=0.2.0"
                }),
                "/livestudio/LiveStudio-Setup.exe" => new HttpResponseMessage(HttpStatusCode.OK),
                "/livestudio/LiveStudio-Setup.exe.sha256" => new HttpResponseMessage(HttpStatusCode.OK),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var service = new ApplicationUpdateService(
            handler,
            "owner",
            "repository",
            new Version(0, 1, 0),
            domesticManifestUri: manifestUri);

        var release = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("download.example.cn", release.PackageDownloadUrl.Host);
        Assert.Equal(3, capturedRequests.Count);
        Assert.DoesNotContain(capturedRequests, request => request.RequestUri?.Host == "github.com");
    }

    [Fact]
    public async Task CheckAsyncFallsBackToGitHubWhenDomesticServiceIsUnavailable()
    {
        var manifestUri = new Uri("https://download.example.cn/livestudio/latest.json");
        var handler = new RouteHandler(request => request.RequestUri?.Host switch
        {
            "download.example.cn" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "release-assets.githubusercontent.com" => new HttpResponseMessage(HttpStatusCode.OK),
            _ when request.RequestUri?.AbsolutePath == "/owner/repository/releases/latest" => Redirect(
                "https://github.com/owner/repository/releases/tag/v0.2.0"),
            _ when request.RequestUri?.AbsolutePath ==
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe" =>
                Redirect("https://release-assets.githubusercontent.com/package"),
            _ when request.RequestUri?.AbsolutePath ==
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe.sha256" =>
                Redirect("https://release-assets.githubusercontent.com/checksum"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new ApplicationUpdateService(
            handler,
            "owner",
            "repository",
            new Version(0, 1, 0),
            domesticManifestUri: manifestUri);

        var release = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("github.com", release.PackageDownloadUrl.Host);
    }

    [Fact]
    public async Task CheckAsyncFallsBackToGitHubWhenDomesticServiceTimesOut()
    {
        var manifestUri = new Uri("https://download.example.cn/livestudio/latest.json");
        var handler = new RouteHandler(request => request.RequestUri?.Host switch
        {
            "download.example.cn" => throw new TaskCanceledException("timeout"),
            "release-assets.githubusercontent.com" => new HttpResponseMessage(HttpStatusCode.OK),
            _ when request.RequestUri?.AbsolutePath == "/owner/repository/releases/latest" => Redirect(
                "https://github.com/owner/repository/releases/tag/v0.2.0"),
            _ when request.RequestUri?.AbsolutePath ==
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe" =>
                Redirect("https://release-assets.githubusercontent.com/package"),
            _ when request.RequestUri?.AbsolutePath ==
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe.sha256" =>
                Redirect("https://release-assets.githubusercontent.com/checksum"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new ApplicationUpdateService(
            handler,
            "owner",
            "repository",
            new Version(0, 1, 0),
            domesticManifestUri: manifestUri);

        var release = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("github.com", release.PackageDownloadUrl.Host);
    }

    [Fact]
    public async Task CheckAsyncRejectsDomesticManifestPointingToAnotherHost()
    {
        var manifestUri = new Uri("https://download.example.cn/livestudio/latest.json");
        var handler = new RouteHandler(_ => JsonResponse(new
        {
            version = "0.2.0",
            tagName = "v0.2.0",
            name = "LiveStudio v0.2.0",
            publishedAt = "2026-09-04T18:00:00+08:00",
            packageUrl = "https://attacker.example/LiveStudio-Setup.exe",
            checksumUrl = "https://attacker.example/LiveStudio-Setup.exe.sha256"
        }));
        var service = new ApplicationUpdateService(
            handler,
            "owner",
            "repository",
            new Version(0, 1, 0),
            domesticManifestUri: manifestUri);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CheckAsync(CancellationToken.None));

        Assert.Contains("不受信任", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsyncReturnsNewerPublicReleaseWithoutAuthorization()
    {
        var capturedRequests = new List<HttpRequestMessage>();
        var handler = new RouteHandler(request =>
        {
            capturedRequests.Add(request);
            return request.RequestUri?.AbsolutePath switch
            {
                "/owner/repository/releases/latest" => Redirect(
                    "https://github.com/owner/repository/releases/tag/v0.2.0"),
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe" =>
                    Redirect("https://release-assets.githubusercontent.com/package"),
                "/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe.sha256" =>
                    Redirect("https://release-assets.githubusercontent.com/checksum"),
                "/package" or "/checksum" => new HttpResponseMessage(HttpStatusCode.OK),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var release = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal(
            "https://github.com/owner/repository/releases/download/v0.2.0/LiveStudio-Setup.exe",
            release.PackageDownloadUrl.ToString());
        Assert.Equal(5, capturedRequests.Count);
        Assert.All(capturedRequests, request => Assert.Null(request.Headers.Authorization));
    }

    [Fact]
    public async Task CheckAsyncReturnsNullWhenReleaseIsCurrentVersion()
    {
        var requestCount = 0;
        var handler = new RouteHandler(_ =>
        {
            requestCount++;
            return Redirect("https://github.com/owner/repository/releases/tag/v0.1.0");
        });
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var release = await service.CheckAsync(CancellationToken.None);

        Assert.Null(release);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task CheckAsyncExplainsWhenPublicRepositoryHasNoRelease()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(CancellationToken.None));

        Assert.Equal("公开更新服务中还没有已发布版本", exception.Message);
    }

    [Fact]
    public async Task CheckAsyncExplainsWhenLatestReleaseHasNoInstallablePackage()
    {
        var handler = new RouteHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/owner/repository/releases/latest" => Redirect(
                "https://github.com/owner/repository/releases/tag/v0.2.0"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CheckAsync(CancellationToken.None));

        Assert.Equal("最新发布中还没有 LiveStudio-Setup.exe", exception.Message);
    }

    [Fact]
    public async Task CheckAsyncRejectsUntrustedLatestReleaseRedirect()
    {
        var handler = new RouteHandler(_ => Redirect("https://example.com/releases/tag/v9.9.9"));
        var service = new ApplicationUpdateService(handler, "owner", "repository", new Version(0, 1, 0));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.CheckAsync(CancellationToken.None));

        Assert.Contains("不受信任", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSignatureVerifierAcceptsTrustedWindowsSignedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signedFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
#pragma warning disable SYSLIB0057
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
#pragma warning restore SYSLIB0057

        ApplicationUpdateSignatureVerifier.Verify(
            signedFile,
            signer.Subject,
            signer.Thumbprint);
    }

    [Fact]
    public void NativeSignatureVerifierRejectsUnsignedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"LiveStudio-Unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(path, "unsigned");

            Assert.Throws<InvalidDataException>(() =>
                ApplicationUpdateSignatureVerifier.Verify(
                    path,
                    "CN=LiveStudio Internal",
                    "4D42933F643E1E0B649513BCD10A15B485746E1D"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value))
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
