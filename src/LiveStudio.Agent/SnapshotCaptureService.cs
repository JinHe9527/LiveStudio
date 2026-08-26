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
    LocalSnapshotIndex snapshotIndex,
    ApplicationOperationGate operationGate)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly IReadOnlyList<IApplicationAdapter> adapters = adapters.ToArray();

    public Task<LocalSnapshotRecord> CaptureAsync(string name, CancellationToken cancellationToken) =>
        CaptureAsync(name, null, cancellationToken);

    public async Task<LocalSnapshotRecord> CaptureAsync(
        string name,
        IReadOnlyList<CameraStationSnapshot>? cameraStations,
        CancellationToken cancellationToken)
    {
        using var operationLease = await operationGate.EnterAsync(cancellationToken);
        return await CaptureCoreAsync(name, NormalizeCameraStations(cameraStations), cancellationToken);
    }

    private async Task<LocalSnapshotRecord> CaptureCoreAsync(
        string name,
        IReadOnlyList<CameraStationSnapshot> cameraStations,
        CancellationToken cancellationToken)
    {
        var credentials = credentialStore.Load();
        EnsureAdapterSet();
        var snapshots = await Task.WhenAll(adapters.Select(adapter => adapter.CaptureAsync(cancellationToken)));
        var inconsistent = snapshots.FirstOrDefault(snapshot => snapshot.CaptureConsistency?.IsConsistent != true);
        if (inconsistent is not null)
        {
            throw new SnapshotCaptureException(
                $"{inconsistent.Kind} 未获得前后哈希一致的在线快照，未生成半份存档");
        }

        var previews = (await Task.WhenAll(adapters.Select(adapter => CapturePreviewSafelyAsync(
                adapter,
                cancellationToken))))
            .Where(preview => preview is not null)
            .Select(preview => preview!)
            .ToArray();
        var assetBindings = SnapshotAssetBindings.Collect(snapshots);
        var assets = assetBindings
            .GroupBy(asset => asset.BlobSha256, StringComparer.Ordinal)
            .Select(group =>
            {
                var binding = group.First();
                var info = new FileInfo(binding.SourcePath);
                return new AssetBlob(
                    binding.BlobSha256,
                    GetMediaType(info.Extension),
                    info.Length,
                    $"assets/{binding.BlobSha256}/content");
            })
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
            3,
            snapshots,
            assets,
            previewReferences,
            cameraStations);
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
            var binding = assetBindings.First(item =>
                string.Equals(item.BlobSha256, asset.Sha256, StringComparison.Ordinal));
            files.Add(await ReadAssetAsync(asset, binding, cancellationToken));
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
            credentials.IsCloudEnrolled,
            credentials.IsCloudEnrolled ? credentials.RoomId : null);
        await snapshotIndex.SaveAsync(record, cancellationToken);
        await SaveLocalIdentityMappingsAsync(snapshots, credentials, cancellationToken);
        return record;
    }

    private static async Task<PreviewCapture?> CapturePreviewSafelyAsync(
        IApplicationAdapter adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.CapturePreviewAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            return null;
        }
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
        AssetBlob asset,
        AssetBinding binding,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(binding.SourcePath))
        {
            throw new SnapshotCaptureException($"找不到滤镜素材: {binding.OriginalFileName}");
        }

        await using var stream = File.OpenRead(binding.SourcePath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, asset.Sha256, StringComparison.Ordinal))
        {
            throw new SnapshotCaptureException($"滤镜素材在保存期间发生变化: {binding.OriginalFileName}");
        }

        var content = await File.ReadAllBytesAsync(binding.SourcePath, cancellationToken);
        return new PackageFile(asset.PackagePath, asset.MediaType, content);
    }

    private static string GetMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".tga" => "image/x-tga",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    internal static IReadOnlyList<CameraStationSnapshot> NormalizeCameraStations(
        IReadOnlyList<CameraStationSnapshot>? stations)
    {
        var normalized = stations is { Count: > 0 }
            ? stations.OrderBy(station => station.Slot).ToArray()
            : DefaultCameraStations();
        if (normalized.Length != 3
            || normalized.Select(station => station.Slot).Distinct().Count() != 3
            || normalized.Any(station => station.Slot is < 0 or > 2))
        {
            throw new SnapshotCaptureException("相机参数必须完整包含主机、游机、侧机三个机位");
        }

        foreach (var station in normalized)
        {
            if (string.IsNullOrWhiteSpace(station.Name)
                || station.Name.Trim().Length > 80
                || string.IsNullOrWhiteSpace(station.Aperture)
                || string.IsNullOrWhiteSpace(station.ShutterSpeed)
                || string.IsNullOrWhiteSpace(station.Iso)
                || string.IsNullOrWhiteSpace(station.CreativeLook)
                || !IsCreativeLookValid(station.CreativeLookSettings))
            {
                throw new SnapshotCaptureException($"{station.Slot + 1} 号机位参数不完整或超出范围");
            }
        }

        return normalized.Select(station => station with
        {
            Name = station.Name.Trim(),
            Aperture = station.Aperture.Trim(),
            ShutterSpeed = station.ShutterSpeed.Trim(),
            Iso = station.Iso.Trim(),
            CreativeLook = station.CreativeLook.Trim().ToUpperInvariant()
        }).ToArray();
    }

    private static bool IsCreativeLookValid(CameraCreativeLookSnapshot settings) =>
        settings.Contrast is >= -9 and <= 9
        && settings.Highlights is >= -9 and <= 9
        && settings.Shadows is >= -9 and <= 9
        && settings.Fade is >= 0 and <= 9
        && settings.Saturation is >= -9 and <= 9
        && settings.Sharpness is >= 0 and <= 9
        && settings.SharpnessRange is >= 0 and <= 5
        && settings.Clarity is >= 0 and <= 9;

    private static CameraStationSnapshot[] DefaultCameraStations() =>
    [
        CreateDefaultCameraStation(0, "主机"),
        CreateDefaultCameraStation(1, "游机"),
        CreateDefaultCameraStation(2, "侧机")
    ];

    private static CameraStationSnapshot CreateDefaultCameraStation(int slot, string name) => new(
        slot,
        name,
        "F4",
        "1/125",
        "640",
        "ST",
        new CameraCreativeLookSnapshot(0, 0, 0, 0, 0, 0, 0, 0));
}
