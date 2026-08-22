using System.Diagnostics;
using System.IO.Compression;
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
    string PayloadRoot,
    string BackupRoot,
    string InstallRoot,
    string DesktopExecutablePath);

public sealed class ApplicationUpdateService(
    HttpMessageHandler? messageHandler = null,
    string repositoryOwner = "JinHe9527",
    string repositoryName = "LiveStudio",
    Version? applicationVersion = null)
{
    private const string WindowsAssetName = "LiveStudio-Windows-x64.zip";
    private const string ChecksumAssetName = "LiveStudio-Windows-x64.zip.sha256";
    private readonly HttpMessageHandler? messageHandler = messageHandler;
    private readonly Version applicationVersion = applicationVersion ?? GetCurrentVersion();

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
        var archivePath = Path.Combine(updateRoot, WindowsAssetName);
        var checksumPath = Path.Combine(updateRoot, ChecksumAssetName);
        using var client = CreateClient(token);
        await DownloadAssetAsync(client, release.PackageApiUrl, archivePath, cancellationToken);
        await DownloadAssetAsync(client, release.ChecksumApiUrl, checksumPath, cancellationToken);
        await VerifyChecksumAsync(archivePath, checksumPath, cancellationToken);

        var payloadDirectory = Path.Combine(updateRoot, "payload");
        if (Directory.Exists(payloadDirectory))
        {
            Directory.Delete(payloadDirectory, true);
        }

        Directory.CreateDirectory(payloadDirectory);
        ExtractArchive(archivePath, payloadDirectory);
        var payloadRoot = ResolvePayloadRoot(payloadDirectory);
        var desktopExecutablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前 LiveStudio 程序路径");
        var desktopDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var installRoot = Directory.GetParent(desktopDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定 LiveStudio 安装目录");
        if (!Directory.Exists(Path.Combine(installRoot, "Agent")))
        {
            throw new InvalidOperationException("当前安装目录缺少 Agent，无法执行完整更新");
        }

        var scriptPath = Path.Combine(updateRoot, "install-update.ps1");
        await File.WriteAllTextAsync(scriptPath, InstallerScript, cancellationToken);
        return new PreparedApplicationUpdate(
            release,
            scriptPath,
            payloadRoot,
            Path.Combine(updateRoot, $"backup-{Guid.NewGuid():N}"),
            installRoot,
            desktopExecutablePath);
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
        startInfo.ArgumentList.Add("-InstallRoot");
        startInfo.ArgumentList.Add(update.InstallRoot);
        startInfo.ArgumentList.Add("-PayloadRoot");
        startInfo.ArgumentList.Add(update.PayloadRoot);
        startInfo.ArgumentList.Add("-BackupRoot");
        startInfo.ArgumentList.Add(update.BackupRoot);
        startInfo.ArgumentList.Add("-DesktopExecutable");
        startInfo.ArgumentList.Add(update.DesktopExecutablePath);
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

    private static void ExtractArchive(string archivePath, string targetDirectory)
    {
        var normalizedTarget = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包包含越界路径");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static string ResolvePayloadRoot(string extractedDirectory)
    {
        if (HasRequiredDirectories(extractedDirectory))
        {
            return extractedDirectory;
        }

        var directories = Directory.GetDirectories(extractedDirectory);
        if (directories.Length == 1 && HasRequiredDirectories(directories[0]))
        {
            return directories[0];
        }

        throw new InvalidDataException("更新包缺少 Desktop 或 Agent 目录");
    }

    private static bool HasRequiredDirectories(string directory) =>
        Directory.Exists(Path.Combine(directory, "Desktop"))
        && Directory.Exists(Path.Combine(directory, "Agent"));

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

    private const string InstallerScript = """
param(
    [Parameter(Mandatory = $true)][int]$DesktopPid,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [Parameter(Mandatory = $true)][string]$BackupRoot,
    [Parameter(Mandatory = $true)][string]$DesktopExecutable
)
$ErrorActionPreference = 'Stop'
try {
    Wait-Process -Id $DesktopPid -Timeout 45 -ErrorAction SilentlyContinue
    Get-Process -Name 'LiveStudio.Agent' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Path -and $_.Path.StartsWith($InstallRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 800
    New-Item -ItemType Directory -Path $BackupRoot | Out-Null
    Get-ChildItem -LiteralPath $InstallRoot | Copy-Item -Destination $BackupRoot -Recurse -Force
    Get-ChildItem -LiteralPath $PayloadRoot | Copy-Item -Destination $InstallRoot -Recurse -Force
    Start-Process -FilePath $DesktopExecutable
} catch {
    $_ | Out-File -FilePath (Join-Path (Split-Path $BackupRoot -Parent) 'install-error.log') -Encoding utf8
    if (Test-Path -LiteralPath $BackupRoot) {
        Get-ChildItem -LiteralPath $BackupRoot | Copy-Item -Destination $InstallRoot -Recurse -Force
        Start-Process -FilePath $DesktopExecutable
    } else {
        throw
    }
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
