using System.Net;
using System.Security.Cryptography.X509Certificates;
using LiveStudio.Desktop.Services;

namespace LiveStudio.Core.Tests;

public sealed class ApplicationUpdateServiceTests
{
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
        Assert.Equal(3, capturedRequests.Count);
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

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
