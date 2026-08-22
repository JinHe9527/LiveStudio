using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;
using LiveStudio.Core;
using LiveStudio.Packaging;

namespace LiveStudio.Agent;

public sealed class SnapshotCaptureException(string message) : Exception(message);

public sealed class SnapshotCaptureService(
    IEnumerable<IApplicationAdapter> adapters,
    IDeviceCredentialStore credentialStore,
    LocalSnapshotIndex snapshotIndex)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly IReadOnlyList<IApplicationAdapter> adapters = adapters.ToArray();

    public async Task<LocalSnapshotRecord> CaptureAsync(string name, CancellationToken cancellationToken)
    {
        var credentials = credentialStore.Load();
        EnsureAdapterSet();
        var statuses = await Task.WhenAll(adapters.Select(adapter => adapter.InspectAsync(cancellationToken)));
        if (statuses.Any(status => !status.CanDetermineLiveState))
        {
            throw new SnapshotCaptureException("无法确定应用是否正在直播，不会关闭应用或保存存档");
        }

        if (statuses.Any(status => status.IsStreaming || status.IsRecording))
        {
            throw new SnapshotCaptureException("开播、推流或录制期间禁止联合保存");
        }

        var snapshots = await Task.WhenAll(adapters.Select(adapter => adapter.CaptureStableAsync(cancellationToken)));
        var previews = (await Task.WhenAll(adapters.Select(adapter => adapter.CapturePreviewAsync(cancellationToken))))
            .Where(preview => preview is not null)
            .Select(preview => preview!)
            .ToArray();
        var assets = snapshots.SelectMany(application => application.Sources)
            .SelectMany(source => source.Filters)
            .SelectMany(filter => filter.Assets)
            .GroupBy(asset => asset.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var previewReferences = previews.Select(preview => new PreviewReference(
            preview.Application,
            preview.MediaType,
            $"previews/{preview.Application.ToString().ToLowerInvariant()}{PreviewExtension(preview.MediaType)}",
            preview.CapturedAt)).ToArray();
        var snapshotId = Guid.NewGuid();
        var combined = new CombinedSnapshot(
            snapshotId,
            credentials.OrganizationId,
            credentials.RoomId,
            name.Trim(),
            DateTimeOffset.UtcNow,
            1,
            snapshots,
            assets,
            previewReferences);
        var files = new List<PackageFile>();
        foreach (var application in snapshots)
        {
            files.Add(new PackageFile(
                $"native/{application.Kind.ToString().ToLowerInvariant()}.json",
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(application, JsonOptions)));
        }

        for (var index = 0; index < previews.Length; index++)
        {
            files.Add(new PackageFile(
                previewReferences[index].PackagePath,
                previews[index].MediaType,
                previews[index].Content));
        }

        foreach (var asset in assets)
        {
            files.Add(await ReadAssetAsync(asset, cancellationToken));
        }

        var snapshotDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveStudio",
            "Snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        var packagePath = Path.Combine(snapshotDirectory, $"{snapshotId:N}.lscfg");
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(credentials.PackageSigningPrivateKeyPem);
        await SnapshotPackageWriter.WriteAsync(
            packagePath,
            combined,
            files,
            signingKey,
            credentials.DeviceId.ToString("N"),
            cancellationToken);
        await using var packageStream = File.OpenRead(packagePath);
        var packageHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(packageStream, cancellationToken));
        var info = new FileInfo(packagePath);
        var record = new LocalSnapshotRecord(
            snapshotId,
            combined.Name,
            packagePath,
            packageHash,
            info.Length,
            combined.CreatedAt,
            false,
            true);
        await snapshotIndex.SaveAsync(record, cancellationToken);
        await SaveLocalIdentityMappingsAsync(snapshots, credentials, cancellationToken);
        return record;
    }

    private static string PreviewExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => throw new SnapshotCaptureException($"不支持的预览图格式: {mediaType}")
    };

    private async Task SaveLocalIdentityMappingsAsync(
        IEnumerable<ApplicationSnapshot> snapshots,
        DeviceCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var application in snapshots)
        {
            foreach (var source in application.Sources.Where(source =>
                         source.Device?.InterfaceHint is { Length: > 0 }))
            {
                await snapshotIndex.SaveMappingAsync(
                    new DeviceMapping(
                        Guid.NewGuid(),
                        credentials.OrganizationId,
                        credentials.DeviceId,
                        source.LogicalId,
                        application.Kind,
                        source.Device!.InterfaceHint!,
                        source.Name,
                        string.Empty,
                        false),
                    cancellationToken);
            }
        }
    }

    private void EnsureAdapterSet()
    {
        var kinds = adapters.Select(adapter => adapter.Kind).ToHashSet();
        if (!kinds.SetEquals([ApplicationKind.Obs, ApplicationKind.LiveCompanion]))
        {
            throw new SnapshotCaptureException("联合保存需要 OBS 和直播伴侣两个可用适配器");
        }
    }

    private static async Task<PackageFile> ReadAssetAsync(
        AssetReference asset,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(asset.SourcePath))
        {
            throw new SnapshotCaptureException($"找不到滤镜素材: {asset.OriginalFileName}");
        }

        await using var stream = File.OpenRead(asset.SourcePath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, asset.Sha256, StringComparison.Ordinal))
        {
            throw new SnapshotCaptureException($"滤镜素材在保存期间发生变化: {asset.OriginalFileName}");
        }

        var content = await File.ReadAllBytesAsync(asset.SourcePath, cancellationToken);
        return new PackageFile(asset.PackagePath, asset.MediaType, content);
    }
}
