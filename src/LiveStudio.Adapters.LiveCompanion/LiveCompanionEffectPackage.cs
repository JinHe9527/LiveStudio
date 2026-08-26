using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStudio.Contracts;

namespace LiveStudio.Adapters.LiveCompanion;

internal static class LiveCompanionEffectPackage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task CreateAsync(
        string packagePath,
        NativeConfigurationDocument effectConfigurationDocument,
        string effectConfigurationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectConfigurationId);
        if (!string.Equals(
                Path.GetFileName(effectConfigurationDocument.RelativePath),
                "effectConfigStore.json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("原生效果包只能由 effectConfigStore.json 生成");
        }

        var storeRoot = new JsonObject();
        foreach (var value in effectConfigurationDocument.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveCompanionConfigurationStore.SetPointer(
                storeRoot,
                value.JsonPointer,
                JsonNode.Parse(value.Value.GetRawText()));
        }

        var configuration = storeRoot["effectConfigStore"]?["configs"]?[effectConfigurationId]
            ?? throw new InvalidOperationException(
                $"存档缺少直播伴侣效果配置 {effectConfigurationId}");
        var content = new JsonObject
        {
            ["config"] = configuration.DeepClone()
        }.ToJsonString(JsonOptions);
        var contentHash = Sha1Hex(content);
        var signaturePayload = JsonSerializer.Serialize(new[] { contentHash }, JsonOptions);
        var manifest = new JsonObject
        {
            ["content"] = content,
            ["files"] = new JsonArray(),
            ["sign"] = Sha1Hex(signaturePayload)
        };

        var fullPath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
                                  ?? throw new InvalidOperationException("无法确定原生效果包目录"));
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = archive.CreateEntry("config.json", CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await JsonSerializer.SerializeAsync(
                    entryStream,
                    manifest,
                    JsonOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static string Sha1Hex(string value)
    {
        // 直播伴侣 12.8.1 的原生效果包协议固定使用 SHA-1 做内容一致性校验；
        // 这不是安全签名，存档本身仍由 LiveStudio 的 SHA-256 和签名链保护。
#pragma warning disable CA5350
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
#pragma warning restore CA5350
    }
}
