using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LiveStudio.Desktop.Services;

public sealed record ApplicationUpdateRelease(
    Version Version,
    string TagName,
    string Name,
    DateTimeOffset PublishedAt,
    Uri PackageApiUrl,
    Uri ChecksumApiUrl);

public sealed record PreparedApplicationUpdate(
    ApplicationUpdateRelease Release,
    string InstallerScriptPath,
    string PackagePath);

public sealed class ApplicationUpdateService(
    HttpMessageHandler? messageHandler = null,
    string repositoryOwner = "JinHe9527",
    string repositoryName = "LiveStudio",
    Version? applicationVersion = null,
    string? trustedPublisher = null,
    string? trustedCertificateThumbprint = null)
{
    private const string WindowsAssetName = "LiveStudio-Windows-x64.msix";
    private const string ChecksumAssetName = "LiveStudio-Windows-x64.msix.sha256";
    private readonly HttpMessageHandler? messageHandler = messageHandler;
    private readonly Version applicationVersion = applicationVersion ?? GetCurrentVersion();
    private readonly string trustedPublisher = trustedPublisher ?? GetAssemblyMetadata("LiveStudioUpdatePublisher");
    private readonly string trustedCertificateThumbprint = NormalizeThumbprint(
        trustedCertificateThumbprint ?? GetAssemblyMetadata("LiveStudioUpdateCertificateThumbprint"));

    public string CurrentVersionText => applicationVersion.ToString(3);

    public async Task<ApplicationUpdateRelease?> CheckAsync(
        string token,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(token);
        using var response = await client.GetAsync(
            $"repos/{repositoryOwner}/{repositoryName}/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub 没有返回 Release 信息");
        var version = ParseVersion(release.TagName);
        if (version <= applicationVersion)
        {
            return null;
        }

        var package = release.Assets.FirstOrDefault(asset => asset.Name == WindowsAssetName)
            ?? throw new InvalidOperationException($"Release 缺少 {WindowsAssetName}");
        var checksum = release.Assets.FirstOrDefault(asset => asset.Name == ChecksumAssetName)
            ?? throw new InvalidOperationException($"Release 缺少 {ChecksumAssetName}");
        return new ApplicationUpdateRelease(
            version,
            release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.PublishedAt,
            package.ApiUrl,
            checksum.ApiUrl);
    }

    public async Task<PreparedApplicationUpdate> PrepareAsync(
        ApplicationUpdateRelease release,
        string token,
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
        using var client = CreateClient(token);
        await DownloadAssetAsync(client, release.PackageApiUrl, packagePath, cancellationToken);
        await DownloadAssetAsync(client, release.ChecksumApiUrl, checksumPath, cancellationToken);
        await VerifyChecksumAsync(packagePath, checksumPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(trustedPublisher)
            || string.IsNullOrWhiteSpace(trustedCertificateThumbprint))
        {
            throw new InvalidOperationException("当前安装包没有内置更新签名身份，禁止安装更新");
        }

        await VerifyPackageIdentityAsync(
            packagePath,
            trustedPublisher,
            trustedCertificateThumbprint,
            cancellationToken);

        var scriptPath = Path.Combine(updateRoot, "install-update.ps1");
        await File.WriteAllTextAsync(scriptPath, InstallerScript, cancellationToken);
        return new PreparedApplicationUpdate(
            release,
            scriptPath,
            packagePath);
    }

    public static void LaunchInstaller(PreparedApplicationUpdate update)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(update.InstallerScriptPath);
        startInfo.ArgumentList.Add("-DesktopPid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-PackagePath");
        startInfo.ArgumentList.Add(update.PackagePath);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新安装程序");
    }

    private HttpClient CreateClient(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var client = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler, false);
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveStudio-Updater");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task DownloadAssetAsync(
        HttpClient client,
        Uri assetApiUrl,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
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

    private static async Task VerifyPackageIdentityAsync(
        string packagePath,
        string trustedPublisher,
        string trustedCertificateThumbprint,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(VerifyPackageScript);
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add(trustedPublisher);
        startInfo.ArgumentList.Add(trustedCertificateThumbprint);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 MSIX 签名验证");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"MSIX 签名身份校验失败：{string.Join(' ', new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim()}");
        }
    }

    private static string GetAssemblyMetadata(string key) =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? string.Empty;

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

    private const string VerifyPackageScript = """
& {
    param($PackagePath, $ExpectedPublisher, $ExpectedThumbprint)
    $ErrorActionPreference = 'Stop'
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "签名状态不是 Valid: $($signature.Status)"
    }
    if (-not $signature.SignerCertificate) {
        throw '安装包没有签名证书'
    }
    $actualThumbprint = $signature.SignerCertificate.Thumbprint.Replace(' ', '')
    if (-not $actualThumbprint.Equals($ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw "证书指纹不匹配: $actualThumbprint"
    }
    if (-not $signature.SignerCertificate.Subject.Equals($ExpectedPublisher, [StringComparison]::Ordinal)) {
        throw "Publisher 不匹配: $($signature.SignerCertificate.Subject)"
    }
} $args[0] $args[1] $args[2]
""";

    private const string InstallerScript = """
param(
    [Parameter(Mandatory = $true)][int]$DesktopPid,
    [Parameter(Mandatory = $true)][string]$PackagePath
)
$ErrorActionPreference = 'Stop'
try {
    Wait-Process -Id $DesktopPid -Timeout 45 -ErrorAction SilentlyContinue
    Get-Process -Name 'LiveStudio.Agent' -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force
    }
    Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown
    $package = Get-AppxPackage -Name 'LiveStudio.BroadcastConfiguration' |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if (-not $package) {
        throw 'MSIX 已安装，但无法读取包身份'
    }
    Start-Process explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!LiveStudio"
} catch {
    $_ | Out-File -FilePath (Join-Path (Split-Path $PackagePath -Parent) 'install-error.log') -Encoding utf8
    throw
}
""";

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset> Assets);

    private sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] Uri ApiUrl);
}
