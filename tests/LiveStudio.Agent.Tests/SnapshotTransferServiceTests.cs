using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using LiveStudio.Agent;
using LiveStudio.Contracts;
using LiveStudio.Packaging;
using Microsoft.Data.Sqlite;

namespace LiveStudio.Agent.Tests;

public sealed class SnapshotTransferServiceTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "LiveStudio.Agent.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeleteRemovesPackageAndIndexTogether()
    {
        var fixture = await CreateFixtureAsync();
        var record = await fixture.AddSnapshotAsync("主画面");

        var result = await fixture.Service.DeleteAsync(record.Id, CancellationToken.None);

        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(record.PackagePath));
        Assert.Null(await fixture.Index.FindAsync(record.Id, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.SnapshotDirectory, "*.deleting-*"));
    }

    [Fact]
    public async Task DeleteAllRemovesEveryManagedPackageAndIndexRecord()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.AddSnapshotAsync("主画面");
        var second = await fixture.AddSnapshotAsync("备用画面");

        var result = await fixture.Service.DeleteAllAsync(CancellationToken.None);

        Assert.Equal(2, result.DeletedCount);
        Assert.False(File.Exists(first.PackagePath));
        Assert.False(File.Exists(second.PackagePath));
        Assert.Empty(await fixture.Index.GetAllAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.SnapshotDirectory, "*.deleting-*"));
    }

    [Fact]
    public async Task DeleteKeepsIndexWhenPackageCannotBeStaged()
    {
        var fixture = await CreateFixtureAsync();
        var record = await fixture.AddSnapshotAsync("正在使用的画面");
        await using var lockStream = new FileStream(
            record.PackagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            fixture.Service.DeleteAsync(record.Id, CancellationToken.None));

        Assert.True(File.Exists(record.PackagePath));
        Assert.NotNull(await fixture.Index.FindAsync(record.Id, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(fixture.SnapshotDirectory, "*.deleting-*"));
    }

    [Fact]
    public async Task DeleteRejectsPackageOutsideManagedDirectoryWithoutChangingIndex()
    {
        var fixture = await CreateFixtureAsync();
        var outsidePath = Path.Combine(testRoot, $"{Guid.NewGuid():N}.lscfg");
        await File.WriteAllTextAsync(outsidePath, "outside");
        var record = await fixture.AddSnapshotRecordAsync("越界存档", outsidePath);

        await Assert.ThrowsAsync<SnapshotPackageException>(() =>
            fixture.Service.DeleteAsync(record.Id, CancellationToken.None));

        Assert.True(File.Exists(outsidePath));
        Assert.NotNull(await fixture.Index.FindAsync(record.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RenameUpdatesDisplayMetadataWithoutChangingSignedPackageFile()
    {
        var fixture = await CreateFixtureAsync();
        var roomId = Guid.NewGuid();
        var record = await fixture.AddSnapshotAsync("20:04", roomId);
        var originalContent = await File.ReadAllBytesAsync(record.PackagePath);

        var result = await fixture.Service.RenameAsync(
            record.Id,
            "主机位暖色",
            CancellationToken.None);

        var renamed = Assert.IsType<LocalSnapshotRecord>(
            await fixture.Index.FindAsync(record.Id, CancellationToken.None));
        Assert.Equal("主机位暖色", result.Name);
        Assert.Equal("主机位暖色", renamed.Name);
        Assert.Equal(roomId, renamed.RoomId);
        Assert.Equal(originalContent, await File.ReadAllBytesAsync(record.PackagePath));
    }

    [Fact]
    public async Task UpdateCameraStationsRewritesSignedPackageAndIndexTogether()
    {
        var fixture = await CreateFixtureAsync();
        var record = await fixture.AddSignedSnapshotAsync("20:04");
        var originalHash = record.Sha256;
        CameraStationSnapshot[] stations =
        [
            CreateCameraStation(0, "主机"),
            CreateCameraStation(1, "游机"),
            CreateCameraStation(2, "侧机")
        ];

        await fixture.Service.UpdateCameraStationsAsync(record.Id, stations, CancellationToken.None);

        var updatedRecord = Assert.IsType<LocalSnapshotRecord>(
            await fixture.Index.FindAsync(record.Id, CancellationToken.None));
        var package = await fixture.Service.ReadLocalAsync(record.Id, CancellationToken.None);
        Assert.NotEqual(originalHash, updatedRecord.Sha256);
        Assert.Equal(stations, package.Snapshot.CameraStations);
        Assert.Equal(stations, package.Manifest.CameraStations);
        Assert.Empty(Directory.EnumerateFiles(fixture.SnapshotDirectory, "*.camera-backup-*"));
    }

    [Fact]
    public async Task UpdateCameraStationsEmbedsSignedReferenceImageAndCanRemoveIt()
    {
        var fixture = await CreateFixtureAsync();
        var record = await fixture.AddSignedSnapshotAsync("三机位参考图");
        var imagePath = Path.Combine(fixture.SnapshotDirectory, "reference.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var validated = await CameraReferenceImageFile.ReadAsync(imagePath, CancellationToken.None);
        CameraStationSnapshot[] stations =
        [
            CreateCameraStation(0, "主机"),
            CreateCameraStation(1, "游机"),
            CreateCameraStation(2, "侧机")
        ];

        await fixture.Service.UpdateCameraStationsAsync(
            record.Id,
            stations,
            [new CameraReferenceImageChange(0, imagePath, validated.Sha256, false)],
            CancellationToken.None);

        var package = await fixture.Service.ReadLocalAsync(record.Id, CancellationToken.None);
        var reference = Assert.IsType<CameraReferenceImageSnapshot>(package.Snapshot.CameraStations![0].ReferenceImage);
        Assert.Equal("camera-images/station-1.png", reference.PackagePath);
        Assert.Equal(validated.Sha256, reference.Sha256);
        Assert.True(package.Files.ContainsKey(reference.PackagePath));

        await fixture.Service.UpdateCameraStationsAsync(
            record.Id,
            stations,
            [new CameraReferenceImageChange(0, null, null, true)],
            CancellationToken.None);

        package = await fixture.Service.ReadLocalAsync(record.Id, CancellationToken.None);
        Assert.Null(package.Snapshot.CameraStations![0].ReferenceImage);
        Assert.DoesNotContain(package.Files.Keys, path =>
            path.StartsWith("camera-images/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadLocalAcceptsOwnSigningKeyAfterCloudDeviceIdChanges()
    {
        var fixture = await CreateFixtureAsync();
        var oldLocalDeviceId = Guid.NewGuid();
        var record = await fixture.AddSignedSnapshotAsync("注册前存档", oldLocalDeviceId.ToString("N"));

        var package = await fixture.Service.ReadLocalAsync(record.Id, CancellationToken.None);

        Assert.Equal("注册前存档", package.Snapshot.Name);
        Assert.NotEqual(oldLocalDeviceId, fixture.Credentials.DeviceId);
    }

    [Fact]
    public async Task ReadLocalMigratesIndexedSnapshotSignedBeforeCredentialRotation()
    {
        var fixture = await CreateFixtureAsync();
        using var legacyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var legacyKeyId = Guid.NewGuid().ToString("N");
        var record = await fixture.AddSignedSnapshotAsync(
            "注册前旧密钥存档",
            legacyKeyId,
            legacyKey.ExportPkcs8PrivateKeyPem());

        var package = await fixture.Service.ReadLocalAsync(record.Id, CancellationToken.None);
        var trusted = await fixture.Index.FindTrustedSignerAsync(legacyKeyId, CancellationToken.None);

        Assert.Equal("注册前旧密钥存档", package.Snapshot.Name);
        Assert.NotNull(trusted);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(legacyKey.ExportSubjectPublicKeyInfo())),
            trusted.FingerprintSha256);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private async Task<TestFixture> CreateFixtureAsync()
    {
        var dataDirectory = Path.Combine(testRoot, "Data");
        var snapshotDirectory = Path.Combine(testRoot, "Snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        var index = new LocalSnapshotIndex(dataDirectory);
        await index.InitializeAsync(CancellationToken.None);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentials = new DeviceCredentials(
            new Uri("https://localhost"),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-secret",
            signingKey.ExportPkcs8PrivateKeyPem(),
            false);
        var credentialStore = new TestCredentialStore(credentials);
        var service = new SnapshotTransferService(credentialStore, index, snapshotDirectory);
        return new TestFixture(index, service, snapshotDirectory, credentials);
    }

    private sealed record TestFixture(
        LocalSnapshotIndex Index,
        SnapshotTransferService Service,
        string SnapshotDirectory,
        DeviceCredentials Credentials)
    {
        public async Task<LocalSnapshotRecord> AddSnapshotAsync(string name, Guid? roomId = null)
        {
            var path = Path.Combine(SnapshotDirectory, $"{Guid.NewGuid():N}.lscfg");
            await File.WriteAllTextAsync(path, name);
            return await AddSnapshotRecordAsync(name, path, roomId);
        }

        public async Task<LocalSnapshotRecord> AddSnapshotRecordAsync(
            string name,
            string path,
            Guid? roomId = null)
        {
            var file = new FileInfo(path);
            var record = new LocalSnapshotRecord(
                Guid.NewGuid(),
                name,
                file.FullName,
                new string('a', 64),
                file.Length,
                DateTimeOffset.UtcNow,
                false,
                false,
                roomId);
            await Index.SaveAsync(record, CancellationToken.None);
            return record;
        }

        public async Task<LocalSnapshotRecord> AddSignedSnapshotAsync(
            string name,
            string? keyId = null,
            string? signingPrivateKeyPem = null)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(SnapshotDirectory, $"{id:N}.lscfg");
            var snapshot = new CombinedSnapshot(
                id,
                Credentials.OrganizationId,
                Credentials.RoomId,
                name,
                DateTimeOffset.UtcNow,
                3,
                [],
                [],
                []);
            using var signingKey = ECDsa.Create();
            signingKey.ImportFromPem(signingPrivateKeyPem ?? Credentials.PackageSigningPrivateKeyPem);
            await SnapshotPackageWriter.WriteAsync(
                path,
                snapshot,
                [],
                signingKey,
                keyId ?? Credentials.DeviceId.ToString("N"),
                CancellationToken.None);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            var info = new FileInfo(path);
            var record = new LocalSnapshotRecord(
                id,
                name,
                path,
                hash,
                info.Length,
                snapshot.CreatedAt,
                false,
                false,
                null);
            await Index.SaveAsync(record, CancellationToken.None);
            return record;
        }
    }

    private sealed class TestCredentialStore : IDeviceCredentialStore
    {
        private readonly DeviceCredentials credentials;

        public TestCredentialStore(DeviceCredentials credentials)
        {
            this.credentials = credentials;
        }

        public void Save(DeviceCredentials value) => throw new NotSupportedException();

        public DeviceCredentials Load() => credentials;

        public bool TryLoad([NotNullWhen(true)] out DeviceCredentials? credentials)
        {
            credentials = this.credentials;
            return true;
        }
    }

    private static CameraStationSnapshot CreateCameraStation(int slot, string name) => new(
        slot,
        name,
        "F4",
        "1/125",
        "640",
        "ST",
        new CameraCreativeLookSnapshot(0, 0, 0, 0, 0, 0, 0, 0));
}
