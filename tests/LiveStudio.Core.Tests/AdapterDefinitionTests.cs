using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;

namespace LiveStudio.Core.Tests;

public sealed class AdapterDefinitionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void VerifyAcceptsSignedCompleteDefinition()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (definitionJson, signatureJson) = Sign(CreateDefinition(), signingKey);

        var verified = AdapterDefinitionVerifier.Verify(
            definitionJson,
            signatureJson,
            keyId => keyId == "catalog" ? ClonePublicKey(signingKey) : null);

        Assert.Equal("live-companion-1", verified.Definition.Id);
    }

    [Fact]
    public void VerifyRejectsCredentialFieldPaths()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var definition = CreateDefinition() with
        {
            Fields = CreateDefinition().Fields.Append(new FieldMappingDefinition(
                "credential",
                UnifiedFieldKind.FilterSetting,
                "main",
                "/account/accessToken",
                "string",
                false,
                false)).ToArray()
        };
        var (definitionJson, signatureJson) = Sign(definition, signingKey);

        var exception = Assert.Throws<AdapterDefinitionException>(() => AdapterDefinitionVerifier.Verify(
            definitionJson,
            signatureJson,
            _ => ClonePublicKey(signingKey)));

        Assert.Contains("凭据", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchAllowsExperimentalOnlyWhenFingerprintMatches()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (definitionJson, signatureJson) = Sign(CreateDefinition(), signingKey);
        var verified = AdapterDefinitionVerifier.Verify(
            definitionJson,
            signatureJson,
            _ => ClonePublicKey(signingKey));

        var result = CompatibilityMatcher.Match("2.0.0", new string('a', 64), [verified]);

        Assert.Equal(AdapterMatchLevel.Experimental, result.Level);
    }

    private static LiveCompanionAdapterDefinition CreateDefinition()
    {
        var fields = new[]
        {
            Field("device", UnifiedFieldKind.DeviceSelection, "/video/device", "string"),
            Field("width", UnifiedFieldKind.Width, "/video/width", "int"),
            Field("height", UnifiedFieldKind.Height, "/video/height", "int"),
            Field("fps", UnifiedFieldKind.FramesPerSecond, "/video/fps", "int"),
            Field("color-space", UnifiedFieldKind.ColorSpace, "/video/colorSpace", "string"),
            Field("color-range", UnifiedFieldKind.ColorRange, "/video/colorRange", "string")
        };
        return new LiveCompanionAdapterDefinition(
            "live-companion-1",
            "1.0.0",
            "1.9.9",
            new string('a', 64),
            [new ConfigurationStoreDefinition("main", ConfigurationStorageKind.JsonFile, "%LocalAppData%/LiveCompanion/config.json", null, true)],
            fields,
            ["/account"],
            new LiveStateRuleDefinition("main", "/broadcast/state", "idle"),
            new ScreenshotRuleDefinition("window", "main"));
    }

    private static FieldMappingDefinition Field(
        string id,
        UnifiedFieldKind kind,
        string path,
        string valueType) => new(id, kind, "main", path, valueType, true, true);

    private static (byte[] Definition, byte[] Signature) Sign(
        LiveCompanionAdapterDefinition definition,
        ECDsa key)
    {
        var definitionJson = JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(definitionJson));
        var signature = new AdapterDefinitionSignature(
            "ECDSA-P256-SHA256",
            "catalog",
            hash,
            Convert.ToBase64String(key.SignData(definitionJson, HashAlgorithmName.SHA256)));
        return (definitionJson, JsonSerializer.SerializeToUtf8Bytes(signature, JsonOptions));
    }

    private static ECDsa ClonePublicKey(ECDsa source)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(source.ExportSubjectPublicKeyInfo(), out _);
        return key;
    }
}
