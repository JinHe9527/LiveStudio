using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveStudio.Desktop.Services;

public sealed record ApplicationUpdateRelease(
    Version Version,
    string TagName,
    string Name,
    DateTimeOffset PublishedAt,
    Uri PackageDownloadUrl,
    Uri ChecksumDownloadUrl);

public sealed record PreparedApplicationUpdate(
    ApplicationUpdateRelease Release,
    string InstallerPath);

internal sealed record DomesticApplicationUpdateManifest(
    string Version,
    string TagName,
    string Name,
    DateTimeOffset PublishedAt,
    string PackageUrl,
    string ChecksumUrl);

public sealed class ApplicationUpdateService(
    HttpMessageHandler? messageHandler = null,
    string repositoryOwner = "JinHe9527",
    string repositoryName = "LiveStudio",
    Version? applicationVersion = null,
    string? trustedPublisher = null,
    string? trustedCertificateThumbprint = null,
    Uri? domesticManifestUri = null)
{
    private const string WindowsAssetName = "LiveStudio-Setup.exe";
    private const string ChecksumAssetName = "LiveStudio-Setup.exe.sha256";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpMessageHandler? messageHandler = messageHandler;
    private readonly Version applicationVersion = applicationVersion ?? GetCurrentVersion();
    private readonly string trustedPublisher = trustedPublisher ?? GetAssemblyMetadata("LiveStudioUpdatePublisher");
    private readonly string trustedCertificateThumbprint = NormalizeThumbprint(
        trustedCertificateThumbprint ?? GetAssemblyMetadata("LiveStudioUpdateCertificateThumbprint"));
    private readonly Uri? domesticManifestUri = domesticManifestUri ?? GetAssemblyMetadataUri(
        "LiveStudioUpdateManifestUrl");

    public string CurrentVersionText => applicationVersion.ToString(3);

    public async Task<ApplicationUpdateRelease?> CheckAsync(
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var domesticResult = await TryCheckDomesticAsync(client, cancellationToken);
        if (domesticResult.Handled)
        {
            return domesticResult.Release;
        }

        return await CheckGitHubAsync(client, cancellationToken);
    }

    private async Task<ApplicationUpdateRelease?> CheckGitHubAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var releaseUri = await ResolveLatestReleaseUriAsync(client, cancellationToken);
        var tagName = ExtractReleaseTag(releaseUri);
        var version = ParseVersion(tagName);
        if (version <= applicationVersion)
        {
            return null;
        }

        var packageUri = CreateReleaseAssetUri(tagName, WindowsAssetName);
        var checksumUri = CreateReleaseAssetUri(tagName, ChecksumAssetName);
        await EnsureAssetPublishedAsync(client, packageUri, WindowsAssetName, cancellationToken);
        await EnsureAssetPublishedAsync(client, checksumUri, ChecksumAssetName, cancellationToken);
        return new ApplicationUpdateRelease(
            version,
            tagName,
            $"LiveStudio {tagName}",
            DateTimeOffset.MinValue,
            packageUri,
            checksumUri);
    }

    private async Task<(bool Handled, ApplicationUpdateRelease? Release)> TryCheckDomesticAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (domesticManifestUri is null)
        {
            return (false, null);
        }

        EnsureTrustedDownloadUri(domesticManifestUri);
        try
        {
            using var response = await client.GetAsync(
                domesticManifestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, null);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var manifest = await JsonSerializer.DeserializeAsync<DomesticApplicationUpdateManifest>(
                stream,
                ManifestJsonOptions,
                cancellationToken) ?? throw new InvalidDataException("国内更新清单为空");
            var version = ParseVersion(manifest.Version);
            if (!ParseVersion(manifest.TagName).Equals(version))
            {
                throw new InvalidDataException("国内更新清单的版本与标签不一致");
            }

            if (version <= applicationVersion)
            {
                return (true, null);
            }

            if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri)
                || !Uri.TryCreate(manifest.ChecksumUrl, UriKind.Absolute, out var checksumUri))
            {
                throw new InvalidDataException("国内更新清单包含无效的下载地址");
            }

            EnsureTrustedDownloadUri(packageUri);
            EnsureTrustedDownloadUri(checksumUri);
            if (!string.Equals(packageUri.Host, domesticManifestUri.Host, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(checksumUri.Host, domesticManifestUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("国内更新清单引用了不同的下载主机");
            }

            await EnsureAssetPublishedAsync(client, packageUri, WindowsAssetName, cancellationToken);
            await EnsureAssetPublishedAsync(client, checksumUri, ChecksumAssetName, cancellationToken);
            return (true, new ApplicationUpdateRelease(
                version,
                manifest.TagName,
                string.IsNullOrWhiteSpace(manifest.Name) ? $"LiveStudio {manifest.TagName}" : manifest.Name,
                manifest.PublishedAt,
                packageUri,
                checksumUri));
        }
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    public async Task<PreparedApplicationUpdate> PrepareAsync(
        ApplicationUpdateRelease release,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("应用内安装更新当前只支持 Windows");
        }

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Updates",
            release.Version.ToString(3));
        Directory.CreateDirectory(updateRoot);
        var packagePath = Path.Combine(updateRoot, WindowsAssetName);
        var checksumPath = Path.Combine(updateRoot, ChecksumAssetName);
        using var client = CreateClient();
        await DownloadAssetAsync(client, release.PackageDownloadUrl, packagePath, cancellationToken);
        await DownloadAssetAsync(client, release.ChecksumDownloadUrl, checksumPath, cancellationToken);
        await VerifyChecksumAsync(packagePath, checksumPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(trustedPublisher)
            || string.IsNullOrWhiteSpace(trustedCertificateThumbprint))
        {
            throw new InvalidOperationException("当前安装包没有内置更新签名身份，禁止安装更新");
        }

        ApplicationUpdateSignatureVerifier.Verify(
            packagePath,
            trustedPublisher,
            trustedCertificateThumbprint);

        return new PreparedApplicationUpdate(release, packagePath);
    }

    public static void LaunchInstaller(PreparedApplicationUpdate update)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = update.InstallerPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(update.InstallerPath)
        };
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新安装程序");
    }

    private HttpClient CreateClient()
    {
        var handler = messageHandler ?? new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler, messageHandler is null);
        client.BaseAddress = new Uri("https://github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveStudio-Updater");
        return client;
    }

    private async Task<Uri> ResolveLatestReleaseUriAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var current = new Uri(
            $"https://github.com/{repositoryOwner}/{repositoryName}/releases/latest");
        for (var redirectCount = 0; redirectCount < 6; redirectCount++)
        {
            using var response = await client.GetAsync(
                current,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("公开更新服务中还没有已发布版本");
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                EnsureTrustedDownloadUri(current);
                if (TryExtractReleaseTag(current, out _))
                {
                    return current;
                }

                continue;
            }

            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? current;
            EnsureTrustedDownloadUri(finalUri);
            if (TryExtractReleaseTag(finalUri, out _))
            {
                return finalUri;
            }

            throw new InvalidDataException("公开更新服务没有返回可识别的最新版本地址");
        }

        throw new InvalidDataException("公开更新服务重定向次数过多");
    }

    private Uri CreateReleaseAssetUri(string tagName, string assetName) => new(
        $"https://github.com/{repositoryOwner}/{repositoryName}/releases/download/{Uri.EscapeDataString(tagName)}/{assetName}");

    private static async Task EnsureAssetPublishedAsync(
        HttpClient client,
        Uri assetUri,
        string assetName,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            assetUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"最新发布中还没有 {assetName}");
        }

        if (!response.IsSuccessStatusCode && !IsRedirect(response.StatusCode))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task DownloadAssetAsync(
        HttpClient client,
        Uri assetApiUrl,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingRedirectsAsync(client, assetApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirectCount = 0; redirectCount < 8; redirectCount++)
        {
            EnsureTrustedDownloadUri(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
        }

        throw new InvalidDataException("更新包下载重定向次数过多");
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode) => statusCode is
        System.Net.HttpStatusCode.MovedPermanently
        or System.Net.HttpStatusCode.Redirect
        or System.Net.HttpStatusCode.RedirectMethod
        or System.Net.HttpStatusCode.TemporaryRedirect
        or System.Net.HttpStatusCode.PermanentRedirect;

    private void EnsureTrustedDownloadUri(Uri uri)
    {
        var trustedHost = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, domesticManifestUri?.Host, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !trustedHost)
        {
            throw new InvalidDataException($"更新服务重定向到不受信任的地址：{uri.Host}");
        }
    }

    private static string ExtractReleaseTag(Uri releaseUri) =>
        TryExtractReleaseTag(releaseUri, out var tagName)
            ? tagName
            : throw new InvalidDataException("无法从公开更新地址解析版本标签");

    private static bool TryExtractReleaseTag(Uri releaseUri, out string tagName)
    {
        var segments = releaseUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tagIndex = Array.FindIndex(segments, segment =>
            string.Equals(segment, "tag", StringComparison.OrdinalIgnoreCase));
        tagName = tagIndex >= 0 && tagIndex + 1 < segments.Length
            ? Uri.UnescapeDataString(segments[tagIndex + 1])
            : string.Empty;
        return !string.IsNullOrWhiteSpace(tagName);
    }

    private static async Task VerifyChecksumAsync(
        string archivePath,
        string checksumPath,
        CancellationToken cancellationToken)
    {
        var checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken);
        var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Release 的 SHA-256 校验文件无效");
        }

        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新包 SHA-256 校验失败，已停止安装");
        }
    }

    private static string GetAssemblyMetadata(string key) =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;

    private static Uri? GetAssemblyMetadataUri(string key)
    {
        var value = GetAssemblyMetadata(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidDataException($"程序集中的 {key} 不是有效地址");
    }

    private static string NormalizeThumbprint(string value) => new(
        value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static Version GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : new Version(0, 0, 0);

    private static Version ParseVersion(string tagName)
    {
        var value = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : throw new InvalidDataException($"无法解析 Release 版本号：{tagName}");
    }

}
