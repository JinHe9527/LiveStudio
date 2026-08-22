using System.Security.Cryptography;
using System.Text.Json;

namespace LiveStudio.Adapters.LiveCompanion;

public sealed class AdapterDefinitionException : Exception
{
    public AdapterDefinitionException(string message)
        : base(message)
    {
    }
}

public static class AdapterDefinitionVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveTerms =
    [
        "token", "cookie", "password", "passwd", "credential", "authorization", "streamkey", "secret", "login"
    ];

    public static VerifiedAdapterDefinition Verify(
        ReadOnlySpan<byte> definitionJson,
        ReadOnlySpan<byte> signatureJson,
        Func<string, ECDsa?> resolveVerificationKey)
    {
        ArgumentNullException.ThrowIfNull(resolveVerificationKey);
        var definition = JsonSerializer.Deserialize<LiveCompanionAdapterDefinition>(definitionJson, JsonOptions)
            ?? throw new AdapterDefinitionException("无法解析直播伴侣适配定义");
        var signature = JsonSerializer.Deserialize<AdapterDefinitionSignature>(signatureJson, JsonOptions)
            ?? throw new AdapterDefinitionException("无法解析适配定义签名");
        if (!string.Equals(signature.Algorithm, "ECDSA-P256-SHA256", StringComparison.Ordinal))
        {
            throw new AdapterDefinitionException("适配定义签名算法无效");
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(definitionJson));
        if (!string.Equals(actualHash, signature.DefinitionSha256, StringComparison.Ordinal))
        {
            throw new AdapterDefinitionException("适配定义 SHA-256 不一致");
        }

        using var key = resolveVerificationKey(signature.KeyId)
            ?? throw new AdapterDefinitionException("找不到适配定义签名密钥");
        if (!key.VerifyData(definitionJson, Convert.FromBase64String(signature.SignatureBase64), HashAlgorithmName.SHA256))
        {
            throw new AdapterDefinitionException("适配定义签名无效");
        }

        ValidateDefinition(definition);
        return new VerifiedAdapterDefinition(definition, signature.KeyId, actualHash);
    }

    private static void ValidateDefinition(LiveCompanionAdapterDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id)
            || !Version.TryParse(definition.MinimumVersion, out var minimum)
            || !Version.TryParse(definition.MaximumVersion, out var maximum)
            || minimum > maximum
            || definition.StructureFingerprint.Length != 64
            || !definition.StructureFingerprint.All(Uri.IsHexDigit))
        {
            throw new AdapterDefinitionException("适配定义的 ID、版本范围或结构指纹无效");
        }

        var storeIds = definition.Stores.Select(store => store.Id).ToHashSet(StringComparer.Ordinal);
        if (storeIds.Count != definition.Stores.Count
            || definition.Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != definition.Fields.Count
            || definition.Fields.Any(field => !storeIds.Contains(field.StoreId))
            || !storeIds.Contains(definition.LiveStateRule.StoreId))
        {
            throw new AdapterDefinitionException("适配定义的存储或字段引用不闭合");
        }

        var paths = definition.Fields.Select(field => field.NativePath)
            .Concat(definition.ExcludedNativePaths)
            .Append(definition.LiveStateRule.NativePath);
        if (paths.Any(path => SensitiveTerms.Any(term => Normalize(path).Contains(term, StringComparison.Ordinal))))
        {
            throw new AdapterDefinitionException("适配定义引用了禁止的账号或凭据字段");
        }

        var requiredKinds = new HashSet<UnifiedFieldKind>
        {
            UnifiedFieldKind.DeviceSelection,
            UnifiedFieldKind.Width,
            UnifiedFieldKind.Height,
            UnifiedFieldKind.FramesPerSecond,
            UnifiedFieldKind.ColorSpace,
            UnifiedFieldKind.ColorRange
        };
        if (!requiredKinds.IsSubsetOf(definition.Fields.Where(field => field.Required).Select(field => field.UnifiedKind)))
        {
            throw new AdapterDefinitionException("适配定义缺少必需的视频参数字段");
        }
    }

    private static string Normalize(string value) => string.Concat(
        value.Where(character => char.IsLetterOrDigit(character))).ToLowerInvariant();
}
