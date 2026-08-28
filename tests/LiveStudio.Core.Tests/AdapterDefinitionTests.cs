using System.Security.Cryptography;
using System.Text.Json;
using LiveStudio.Adapters.LiveCompanion;
using LiveStudio.Contracts;

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

    [Fact]
    public void CatalogLoadsDefinitionOnlyWithMatchingTrustedSignature()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"livestudio-adapter-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "trusted-keys"));
        try
        {
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (definitionJson, signatureJson) = Sign(CreateDefinition(), signingKey);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.adapter.json"), definitionJson);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.signature.json"), signatureJson);
            File.WriteAllText(
                Path.Combine(directory, "trusted-keys", "catalog.pem"),
                signingKey.ExportSubjectPublicKeyInfoPem());

            var catalog = new LiveCompanionAdapterCatalog(directory);

            var adapter = Assert.Single(catalog.GetAll());
            Assert.Equal("live-companion-1", adapter.Definition.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RequiredShapeMatchAllowsMissingApplicationManagedOptionalFields()
    {
        var baseDefinition = CreateDefinition();
        var definition = baseDefinition with
        {
            Stores = [new ConfigurationStoreDefinition(
                "main",
                ConfigurationStorageKind.JsonFile,
                "config.json",
                null,
                true)],
            Fields = baseDefinition.Fields.Append(new FieldMappingDefinition(
                "runtime-cache",
                UnifiedFieldKind.NativeField,
                "main",
                "/runtime/generatedId",
                "string",
                false,
                false,
                ControlKind: "ApplicationManaged")).ToArray()
        };

        Assert.True(LiveCompanionAdapterCatalog.MatchesRequiredShape(
            definition,
            [CreateDiscoveredDocument(definition.Fields.Where(field => field.Required))]));
    }

    [Fact]
    public void RequiredShapeMatchRejectsMissingOrWrongTypeRequiredFields()
    {
        var baseDefinition = CreateDefinition();
        var definition = baseDefinition with
        {
            Stores = [new ConfigurationStoreDefinition(
                "main",
                ConfigurationStorageKind.JsonFile,
                "config.json",
                null,
                true)]
        };
        var missingWidth = definition.Fields.Where(field => field.Id != "width");
        var wrongWidth = definition.Fields.Select(field => field.Id == "width"
            ? field with { ValueType = "string" }
            : field);

        Assert.False(LiveCompanionAdapterCatalog.MatchesRequiredShape(
            definition,
            [CreateDiscoveredDocument(missingWidth)]));
        Assert.False(LiveCompanionAdapterCatalog.MatchesRequiredShape(
            definition,
            [CreateDiscoveredDocument(wrongWidth)]));
    }

    [Fact]
    public void CrossVersionCompatibilityAllowsDeclaredOptionalFieldsAndRejectsUnknownFields()
    {
        var baseDefinition = CreateDefinition();
        var optional = new FieldMappingDefinition(
            "optional-new-version-field",
            UnifiedFieldKind.NativeField,
            "main",
            "/video/optionalNewVersionField",
            "number",
            false,
            true);
        var definition = baseDefinition with
        {
            Stores = [new ConfigurationStoreDefinition(
                "main",
                ConfigurationStorageKind.JsonFile,
                "config.json",
                null,
                true)],
            Fields = baseDefinition.Fields.Append(optional).ToArray()
        };
        var withoutOptional = CreateDiscoveredDocument(baseDefinition.Fields);
        var withOptional = CreateDiscoveredDocument(definition.Fields);
        var unknownValues = withOptional.Values.Append(new NativeConfigurationValue(
            "/video/newVersionField",
            NativeParameterCategories.Filter,
            JsonSerializer.SerializeToElement(1))).ToArray();

        Assert.True(LiveCompanionAdapterCatalog.MatchesCompatibleShape(definition, [withoutOptional]));
        Assert.True(LiveCompanionAdapterCatalog.MatchesCompatibleShape(definition, [withOptional]));
        Assert.False(LiveCompanionAdapterCatalog.MatchesCompatibleShape(
            definition,
            [withOptional with { Values = unknownValues }]));
    }

    [Fact]
    public void LegacyDiscoverySnapshotIsPromotedOnlyForACompleteSignedShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"livestudio-adapter-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "trusted-keys"));
        try
        {
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var baseDefinition = CreateDefinition();
            var definition = baseDefinition with
            {
                Stores = [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    "config.json",
                    null,
                    true)],
                Fields = baseDefinition.Fields.Select((field, index) => field with
                {
                    NativeName = field.Id,
                    UiPath = $"基础设置/{field.Id}",
                    Order = index,
                    ControlKind = "NativeValue",
                    EvidenceStatus = FieldEvidenceStatus.Mapped
                }).ToArray()
            };
            var (definitionJson, signatureJson) = Sign(definition, signingKey);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.adapter.json"), definitionJson);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.signature.json"), signatureJson);
            File.WriteAllText(
                Path.Combine(directory, "trusted-keys", "catalog.pem"),
                signingKey.ExportSubjectPublicKeyInfoPem());
            var catalog = new LiveCompanionAdapterCatalog(directory);
            var adapter = new LiveCompanionAdapter(catalog);
            var discovered = CreateDiscoveredDocument(definition.Fields);
            var snapshot = new ApplicationSnapshot(
                ApplicationKind.LiveCompanion,
                "2.0.0",
                "webcast-mate-json-discovery",
                string.Empty,
                new string('b', 64),
                CompatibilityLevel.Unsupported,
                false,
                [],
                [],
                [discovered]);

            var promoted = adapter.PrepareRestoreSnapshot(snapshot);

            Assert.Equal(CompatibilityLevel.Verified, promoted.Compatibility);
            Assert.Equal(definition.Id, promoted.AdapterId);
            Assert.Equal(catalog.GetAll().Single().DefinitionSha256, promoted.AdapterDefinitionSha256);
            Assert.Equal(definition.StructureFingerprint, promoted.StructureFingerprint);
            Assert.Equal("main", Assert.Single(promoted.NativeDocuments).StoreId);
            Assert.NotEmpty(promoted.FieldCoverage);
            Assert.True(promoted.ConfigurationTree?.HasCompleteUiInventory);
            Assert.True(promoted.ConfigurationTree?.HasCompleteNativeInventory);

            var unknown = discovered with
            {
                Values = discovered.Values.Append(new NativeConfigurationValue(
                    "/video/unknown",
                    NativeParameterCategories.Unmapped,
                    JsonSerializer.SerializeToElement(1))).ToArray()
            };
            var rejected = adapter.PrepareRestoreSnapshot(snapshot with { NativeDocuments = [unknown] });

            Assert.Equal("webcast-mate-json-discovery", rejected.AdapterId);
            Assert.Equal(CompatibilityLevel.Unsupported, rejected.Compatibility);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CatalogAllowsNewVersionOnlyWhenEveryRequiredPathAndTypeMatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"livestudio-adapter-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "trusted-keys"));
        try
        {
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var definition = CreateDefinition() with
            {
                Stores = [new ConfigurationStoreDefinition(
                    "main",
                    ConfigurationStorageKind.JsonFile,
                    "config.json",
                    null,
                    true)]
            };
            var (definitionJson, signatureJson) = Sign(definition, signingKey);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.adapter.json"), definitionJson);
            File.WriteAllBytes(Path.Combine(directory, "live-companion-1.signature.json"), signatureJson);
            File.WriteAllText(
                Path.Combine(directory, "trusted-keys", "catalog.pem"),
                signingKey.ExportSubjectPublicKeyInfoPem());
            var catalog = new LiveCompanionAdapterCatalog(directory);
            var discovered = new[] { CreateDiscoveredDocument(definition.Fields) };

            var captureMatch = catalog.Match("2.0.0", new string('b', 64), discovered);
            var restoreMatch = catalog.MatchSnapshot(
                "2.0.0",
                definition.Id,
                captureMatch.Adapter!.DefinitionSha256,
                definition.StructureFingerprint,
                discovered);

            Assert.Equal(AdapterMatchLevel.Verified, captureMatch.Level);
            Assert.Equal(AdapterMatchLevel.Verified, restoreMatch.Level);
            Assert.Contains("版本号已变化", restoreMatch.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BundledCurrentMachineAdapterIsSignedCompleteAndExactlyMatched()
    {
        var repositoryRoot = FindRepositoryRoot();
        var catalog = new LiveCompanionAdapterCatalog(Path.Combine(
            repositoryRoot,
            "src",
            "LiveStudio.Agent",
            "Adapters"));

        var match = catalog.Match(
            "12.8.1.454484231",
            "68ba3cc2b53cc19deaff9633f7d2e1ab1dbd36345ae44ef6e234b830c25816b1");
        var snapshotMatch = catalog.MatchSnapshot(
            "12.8.1.454484231",
            "webcast-mate-12.8.1.454484231-68ba3cc2-v2",
            match.Adapter!.DefinitionSha256,
            "68ba3cc2b53cc19deaff9633f7d2e1ab1dbd36345ae44ef6e234b830c25816b1");
        var alteredSnapshot = catalog.MatchSnapshot(
            "12.8.1.454484231",
            "webcast-mate-12.8.1.454484231-68ba3cc2-v2",
            new string('0', 64),
            "68ba3cc2b53cc19deaff9633f7d2e1ab1dbd36345ae44ef6e234b830c25816b1");
        var structuralMatch = catalog.Match(
            "12.9.2.470033184",
            new string('f', 64),
            CreateStructurallyCompatibleDocuments(match.Adapter.Definition));

        Assert.Equal(AdapterMatchLevel.Verified, match.Level);
        Assert.Equal(AdapterMatchLevel.Verified, snapshotMatch.Level);
        Assert.Equal(AdapterMatchLevel.Incompatible, alteredSnapshot.Level);
        Assert.Equal(
            "webcast-mate-12.8.1.454484231-8216f9ee-v3",
            structuralMatch.Adapter?.Definition.Id);
        var definition = Assert.IsType<LiveCompanionAdapterDefinition>(match.Adapter?.Definition);
        Assert.Equal("webcast-mate-12.8.1.454484231-68ba3cc2-v2", definition.Id);
        Assert.Equal(4, definition.Stores.Count);
        Assert.Equal(1028, definition.Fields.Count);
        Assert.Equal(1018, definition.Fields.Count(field => field.Writable));
        Assert.Equal(10, definition.Fields.Count(field => !field.Writable));
        Assert.Equal(966, definition.Fields.Count(LiveCompanionConfigurationStore.IsRestorableField));
        var activityCapabilityInventory = definition.Fields.Where(field =>
            string.Equals(field.StoreId, "effect-store", StringComparison.Ordinal)
            && field.NativePath.StartsWith(
                "/effectStore/carnivalInfo/sourceLink/",
                StringComparison.Ordinal)).ToArray();
        Assert.Equal(52, activityCapabilityInventory.Length);
        Assert.All(activityCapabilityInventory, field =>
            Assert.False(LiveCompanionConfigurationStore.IsRestorableField(field)));
        Assert.All(
            definition.Fields.Where(field =>
                field.Required
                && field.Writable
                && !activityCapabilityInventory.Contains(field)),
            field => Assert.True(LiveCompanionConfigurationStore.IsRestorableField(field)));
        Assert.All(
            definition.Fields.Where(field =>
                string.Equals(field.StoreId, "effect-store", StringComparison.Ordinal)
                && field.NativePath is "/effectStore/carnivalInfo/isOn"
                    or "/effectStore/carnivalInfo/using"),
            field => Assert.True(LiveCompanionConfigurationStore.IsRestorableField(field)));
        Assert.All(definition.Fields.Where(field => field.Writable), field => Assert.True(field.Required));
        Assert.All(definition.Fields.Where(field => !field.Writable), field => Assert.False(field.Required));
        Assert.All(definition.Fields, field => Assert.Equal(FieldEvidenceStatus.Mapped, field.EvidenceStatus));
        Assert.All(
            definition.Fields.Where(field => !field.Writable),
            field => Assert.Equal("ApplicationManaged", field.ControlKind));

        var versionFlexible = catalog.GetAll().Single(adapter =>
            string.Equals(
                adapter.Definition.Id,
                "webcast-mate-12.8.1.454484231-8216f9ee-v3",
                StringComparison.Ordinal));
        Assert.Equal(1042, versionFlexible.Definition.Fields.Count);
        Assert.Equal(14, versionFlexible.Definition.Fields.Count(field =>
            !field.Required && field.Writable));
        Assert.Equal(980, versionFlexible.Definition.Fields.Count(
            LiveCompanionConfigurationStore.IsRestorableField));
        Assert.Equal(966, versionFlexible.Definition.Fields.Count(
            LiveCompanionConfigurationStore.IsRequiredRestorableField));
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

    private static NativeConfigurationDocument CreateDiscoveredDocument(
        IEnumerable<FieldMappingDefinition> fields) => new(
        "webcast_mate",
        "JsonFile",
        "json-v1",
        "config.json",
        "config.json",
        new string('0', 64),
        Guid.NewGuid(),
        fields.Select(field => new NativeConfigurationValue(
            field.NativePath,
            NativeParameterCategories.Filter,
            field.ValueType switch
            {
                "string" => JsonSerializer.SerializeToElement("value"),
                "bool" or "boolean" => JsonSerializer.SerializeToElement(true),
                "object" => JsonSerializer.SerializeToElement(new { value = 1 }),
                "array" => JsonSerializer.SerializeToElement(new List<int> { 1 }),
                "null" => JsonSerializer.SerializeToElement<object?>(null),
                _ => JsonSerializer.SerializeToElement(1)
            })).ToArray());

    private static NativeConfigurationDocument[] CreateStructurallyCompatibleDocuments(
        LiveCompanionAdapterDefinition definition)
    {
        var canonicalEffectId = definition.Fields
            .Select(field => field.NativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(segments => segments.Length >= 3
                               && segments[0] == "effectConfigStore"
                               && segments[1] == "configs")
            .Select(segments => segments[2])
            .First();
        return definition.Stores.Select(store =>
        {
            var values = definition.Fields
                .Where(field => string.Equals(field.StoreId, store.Id, StringComparison.Ordinal))
                .Select(field => new NativeConfigurationValue(
                    field.NativePath,
                    NativeParameterCategories.Filter,
                    field.NativePath.EndsWith("/effectConfigId", StringComparison.Ordinal)
                        ? JsonSerializer.SerializeToElement(canonicalEffectId)
                        : field.NativePath.EndsWith("/type", StringComparison.Ordinal)
                          && field.NativePath.Contains("/sourceStore/sceneSource/", StringComparison.Ordinal)
                          && string.Equals(field.ValueType, "string", StringComparison.Ordinal)
                            ? JsonSerializer.SerializeToElement("camera")
                            : field.ValueType switch
                            {
                                "string" => JsonSerializer.SerializeToElement("value"),
                                "bool" or "boolean" => JsonSerializer.SerializeToElement(true),
                                "object" => JsonSerializer.SerializeToElement(new { value = 1 }),
                                "array" => JsonSerializer.SerializeToElement(new List<int> { 1 }),
                                "null" => JsonSerializer.SerializeToElement<object?>(null),
                                _ => JsonSerializer.SerializeToElement(1)
                            }))
                .ToArray();
            return new NativeConfigurationDocument(
                store.Id,
                store.Kind.ToString(),
                "json-v1",
                store.Location,
                store.Location,
                new string('0', 64),
                Guid.NewGuid(),
                values);
        }).ToArray();
    }

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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiveStudio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 LiveStudio 仓库根目录");
    }
}
