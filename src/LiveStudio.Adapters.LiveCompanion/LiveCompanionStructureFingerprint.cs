using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

public static class LiveCompanionStructureFingerprint
{
    public static string Compute(IEnumerable<NativeConfigurationDocument> documents)
    {
        var structure = documents
            .OrderBy(document => document.StoreId, StringComparer.Ordinal)
            .ThenBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(document => new
            {
                document.StoreId,
                document.StorageKind,
                document.StructureVersion,
                document.RelativePath,
                Fields = document.Values
                    .OrderBy(value => value.JsonPointer, StringComparer.Ordinal)
                    .Select(value => new
                    {
                        value.JsonPointer,
                        value.Category,
                        Type = value.Value.ValueKind.ToString()
                    })
            });
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(structure)));
    }
}
