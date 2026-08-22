using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LiveStudio.Contracts;
using LiveStudio.Packaging;

namespace LiveStudio.Core.Tests;

public sealed class SnapshotPackageTests
{
    [Fact]
    public async Task PackageRoundTripsAndVerifiesSignature()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "snapshot.lscfg");
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicParameters = privateKey.ExportParameters(false);
            var snapshot = CreateSnapshot();
            await SnapshotPackageWriter.WriteAsync(
                path,
                snapshot,
                [new PackageFile("assets/lut.cube", "application/octet-stream", Encoding.UTF8.GetBytes("LUT_3D_SIZE 2"))],
                privateKey,
                "test-key",
                CancellationToken.None);

            var package = await SnapshotPackageReader.ReadAsync(
                path,
                keyId => keyId == "test-key" ? CreatePublicKey(publicParameters) : null,
                CancellationToken.None);

            Assert.Equal(snapshot.Id, package.Snapshot.Id);
            Assert.Contains("assets/lut.cube", package.Files.Keys);
            Assert.Contains("parameters.json", package.Files.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageRejectsSensitiveNativeConfiguration()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var exception = await Assert.ThrowsAsync<SnapshotPackageException>(() => SnapshotPackageWriter.WriteAsync(
                Path.Combine(directory, "snapshot.lscfg"),
                CreateSnapshot(),
                [new PackageFile("native/config.json", "application/json", Encoding.UTF8.GetBytes("{\"token\":\"forbidden\"}"))],
                privateKey,
                "test-key",
                CancellationToken.None));

            Assert.Contains("敏感字段", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageRejectsModifiedEntry()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "snapshot.lscfg");
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicParameters = privateKey.ExportParameters(false);
            await SnapshotPackageWriter.WriteAsync(path, CreateSnapshot(), [], privateKey, "test-key", CancellationToken.None);

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("parameters.json") ?? throw new InvalidOperationException();
                entry.Delete();
                var changed = archive.CreateEntry("parameters.json");
                await using var stream = changed.Open();
                await stream.WriteAsync("{}"u8.ToArray());
            }

            await Assert.ThrowsAsync<SnapshotPackageException>(() => SnapshotPackageReader.ReadAsync(
                path,
                _ => CreatePublicKey(publicParameters),
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageInspectionReturnsEmbeddedSignerIdentity()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "snapshot.lscfg");
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await SnapshotPackageWriter.WriteAsync(
                path,
                CreateSnapshot(),
                [],
                privateKey,
                "source-device",
                CancellationToken.None);

            var inspection = await SnapshotPackageReader.InspectAsync(path, CancellationToken.None);
            var expectedFingerprint = Convert.ToHexStringLower(
                SHA256.HashData(privateKey.ExportSubjectPublicKeyInfo()));

            Assert.Equal("source-device", inspection.Signer.KeyId);
            Assert.Equal(expectedFingerprint, inspection.Signer.FingerprintSha256);
            Assert.Contains("PUBLIC KEY", inspection.Signer.PublicKeyPem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackageRejectsTrustedSignerKeyMismatch()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "snapshot.lscfg");
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var conflictingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var conflictingParameters = conflictingKey.ExportParameters(false);
            await SnapshotPackageWriter.WriteAsync(
                path,
                CreateSnapshot(),
                [],
                signingKey,
                "source-device",
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<SnapshotPackageException>(() =>
                SnapshotPackageReader.ReadAsync(
                    path,
                    keyId => keyId == "source-device"
                        ? CreatePublicKey(conflictingParameters)
                        : null,
                    CancellationToken.None));

            Assert.Contains("公钥与受信任记录不一致", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreImportsListsAndReadsVerifiedPackage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceDirectory = Path.Combine(directory, "source");
            var libraryDirectory = Path.Combine(directory, "library");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "snapshot.lscfg");
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var snapshot = CreateSnapshot();
            await SnapshotPackageWriter.WriteAsync(
                sourcePath,
                snapshot,
                [],
                privateKey,
                "source-device",
                CancellationToken.None);
            var store = new SnapshotFileStore(libraryDirectory);

            var imported = await store.ImportAsync(sourcePath, CancellationToken.None);
            var loaded = await store.GetAllAsync(CancellationToken.None);
            var detail = await store.GetAsync(snapshot.Id, CancellationToken.None);

            Assert.Equal(snapshot.Id, imported.Inspection.Package.Snapshot.Id);
            Assert.Equal(Path.Combine(libraryDirectory, $"{snapshot.Id:N}.lscfg"), imported.PackagePath);
            Assert.Empty(loaded.Errors);
            Assert.Single(loaded.Snapshots);
            Assert.Equal(snapshot.Id, detail.Inspection.Package.Snapshot.Id);
            Assert.Equal("source-device", detail.Inspection.Signer.KeyId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileStoreRejectsDifferentContentWithSameSnapshotId()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "first.lscfg");
            var secondPath = Path.Combine(directory, "second.lscfg");
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var first = CreateSnapshot();
            var second = first with { Name = "同 ID 的不同存档" };
            await SnapshotPackageWriter.WriteAsync(
                firstPath,
                first,
                [],
                privateKey,
                "source-device",
                CancellationToken.None);
            await SnapshotPackageWriter.WriteAsync(
                secondPath,
                second,
                [],
                privateKey,
                "source-device",
                CancellationToken.None);
            var store = new SnapshotFileStore(Path.Combine(directory, "library"));
            await store.ImportAsync(firstPath, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<SnapshotPackageException>(() =>
                store.ImportAsync(secondPath, CancellationToken.None));

            Assert.Contains("已存在", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PackagePreservesLiveCompanionNativeConfigurationValues()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "native-fields.lscfg");
            var sourceId = Guid.NewGuid();
            var nativeValue = new NativeConfigurationValue(
                "/studio/filterChain",
                "Filter",
                System.Text.Json.JsonSerializer.SerializeToElement(new[]
                {
                    new { name = "人像调色", contrast = 0.12 }
                }));
            var nativeDocument = new NativeConfigurationDocument(
                "webcast_mate",
                "studio.json",
                new string('a', 64),
                sourceId,
                [nativeValue]);
            var snapshot = CreateSnapshot() with
            {
                Applications =
                [
                    new ApplicationSnapshot(
                        ApplicationKind.LiveCompanion,
                        "9.2.0",
                        "fingerprint",
                        [],
                        [nativeDocument])
                ]
            };
            using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            await SnapshotPackageWriter.WriteAsync(
                path,
                snapshot,
                [],
                privateKey,
                "source-device",
                CancellationToken.None);
            var inspection = await SnapshotPackageReader.InspectAsync(path, CancellationToken.None);

            var application = Assert.Single(inspection.Package.Snapshot.Applications);
            var restoredDocument = Assert.Single(application.NativeDocuments);
            var restoredValue = Assert.Single(restoredDocument.Values);
            Assert.Equal("/studio/filterChain", restoredValue.JsonPointer);
            Assert.Equal(0.12, restoredValue.Value[0].GetProperty("contrast").GetDouble());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CombinedSnapshot CreateSnapshot() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "画面存档",
        DateTimeOffset.UtcNow,
        1,
        [new ApplicationSnapshot(ApplicationKind.Obs, "32.0.0", "fingerprint", [], [])],
        [],
        []);

    private static ECDsa CreatePublicKey(ECParameters parameters)
    {
        var key = ECDsa.Create();
        key.ImportParameters(parameters);
        return key;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestudio-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
