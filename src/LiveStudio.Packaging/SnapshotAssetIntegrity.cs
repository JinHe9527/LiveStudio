using System.Security.Cryptography;
using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

internal static class SnapshotAssetIntegrity
{
    internal static void Validate(CombinedSnapshot snapshot, IReadOnlyDictionary<string, PackageFile> files)
    {
        var blobs = new Dictionary<string, AssetBlob>(StringComparer.Ordinal);
        foreach (var blob in snapshot.Assets)
        {
            if (!blobs.TryAdd(blob.Sha256, blob))
                throw new SnapshotPackageException($"素材哈希重复：{blob.Sha256}");
            if (!files.TryGetValue(blob.PackagePath, out var file)
                || blob.Length <= 0 || file.Content.Length != blob.Length
                || file.MediaType != blob.MediaType
                || Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)) != blob.Sha256)
                throw new SnapshotPackageException($"素材缺失或内容校验不一致，未生成存档：{blob.PackagePath}");
        }
        foreach (var binding in SnapshotAssetBindings.Collect(snapshot.Applications))
        {
            if (!blobs.TryGetValue(binding.BlobSha256, out var blob) || binding.Length != blob.Length)
                throw new SnapshotPackageException($"素材引用没有完整文件，未生成存档：{binding.OriginalFileName}");
        }
    }
}
