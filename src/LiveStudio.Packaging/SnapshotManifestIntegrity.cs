using System.Security.Cryptography;
using System.Text.Json;

namespace LiveStudio.Packaging;

internal static class SnapshotManifestIntegrity
{
    public static string HashFieldCoverage(IEnumerable<string> paths)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(paths.ToArray());
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
