using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using LiveStudio.Contracts;
using LiveStudio.Core;
using Microsoft.Data.Sqlite;

namespace LiveStudio.Agent.Tests;

public sealed class SnapshotCaptureTransactionTests
{
    [Fact]
    public async Task CaptureUsesStableAdapterBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiveStudioCaptureTests", Guid.NewGuid().ToString("N"));
        var indexRoot = Path.Combine(root, "index");
        var snapshotRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(root);
        try
        {
            var index = new LocalSnapshotIndex(indexRoot);
            await index.InitializeAsync(CancellationToken.None);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var credentials = new DeviceCredentials(
                new Uri("https://local.livestudio.invalid"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                string.Empty,
                signingKey.ExportPkcs8PrivateKeyPem(),
                false);
            using var operationGate = new ApplicationOperationGate();
            var obs = new CaptureAdapter(ApplicationKind.Obs, includeDevice: false);
            var companion = new CaptureAdapter(ApplicationKind.LiveCompanion, includeDevice: false);
            var service = new SnapshotCaptureService(
                [obs, companion],
                new TestCredentialStore(credentials),
                index,
                operationGate,
                snapshotRoot);

            var snapshot = await service.CaptureAsync("稳定读取测试", CancellationToken.None);

            Assert.Equal(0, obs.DirectCaptureCount);
            Assert.Equal(1, obs.StableCaptureCount);
            Assert.Equal(0, companion.DirectCaptureCount);
            Assert.Equal(1, companion.StableCaptureCount);
            Assert.True(File.Exists(snapshot.PackagePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MappingFailureRollsBackIndexAndDeletesGeneratedPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiveStudioCaptureTests", Guid.NewGuid().ToString("N"));
        var indexRoot = Path.Combine(root, "index");
        var snapshotRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(root);
        try
        {
            var index = new LocalSnapshotIndex(indexRoot);
            await index.InitializeAsync(CancellationToken.None);
            await DropDeviceMappingsTableAsync(indexRoot);
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var credentials = new DeviceCredentials(
                new Uri("https://local.livestudio.invalid"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                string.Empty,
                signingKey.ExportPkcs8PrivateKeyPem(),
                false);
            using var operationGate = new ApplicationOperationGate();
            var service = new SnapshotCaptureService(
                [
                    new CaptureAdapter(ApplicationKind.Obs, includeDevice: true),
                    new CaptureAdapter(ApplicationKind.LiveCompanion, includeDevice: false)
                ],
                new TestCredentialStore(credentials),
                index,
                operationGate,
                snapshotRoot);

            await Assert.ThrowsAsync<SqliteException>(
                () => service.CaptureAsync("事务失败测试", CancellationToken.None));

            Assert.Empty(await index.GetAllAsync(CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(snapshotRoot, "*.lscfg"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DropDeviceMappingsTableAsync(string indexRoot)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(indexRoot, "index.db")
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE device_mappings;";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CaptureAdapter(ApplicationKind kind, bool includeDevice) : IApplicationAdapter
    {
        public ApplicationKind Kind { get; } = kind;

        public int DirectCaptureCount { get; private set; }

        public int StableCaptureCount { get; private set; }

        public Task<ApplicationRuntimeStatus> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationRuntimeStatus(true, false, false, "1.0.0", true));

        public Task<ApplicationSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            DirectCaptureCount++;
            return CreateSnapshot();
        }

        public Task<ApplicationSnapshot> CaptureStableAsync(CancellationToken cancellationToken)
        {
            StableCaptureCount++;
            return CreateSnapshot();
        }

        private Task<ApplicationSnapshot> CreateSnapshot()
        {
            IReadOnlyList<VideoSource> sources = includeDevice
                ?
                [
                    new VideoSource(
                        Guid.NewGuid(),
                        "测试采集卡",
                        "video_capture",
                        new CaptureDeviceDescriptor(
                            "测试采集卡",
                            null,
                            null,
                            null,
                            "test-interface",
                            []),
                        null,
                        new Dictionary<string, System.Text.Json.JsonElement>(),
                        [])
                ]
                : [];
            return Task.FromResult(new ApplicationSnapshot(
                Kind,
                "1.0.0",
                "test-adapter",
                new string('a', 64),
                new string('b', 64),
                CompatibilityLevel.Experimental,
                false,
                [],
                sources,
                [],
                CaptureConsistency: new CaptureConsistency(
                    "double-read",
                    new string('c', 64),
                    new string('c', 64),
                    1,
                    true)));
        }

        public Task<PreviewCapture?> CapturePreviewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PreviewCapture?>(null);

        public Task<RestorePreflightResult> PreflightAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(RestorePreflightResult.Success);

        public Task<IApplicationRestoreSession> BeginRestoreAsync(
            RestoreExecutionContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestCredentialStore(DeviceCredentials credentials) : IDeviceCredentialStore
    {
        public void Save(DeviceCredentials value) => throw new NotSupportedException();

        public DeviceCredentials Load() => credentials;

        public bool TryLoad([NotNullWhen(true)] out DeviceCredentials? value)
        {
            value = credentials;
            return true;
        }
    }
}
