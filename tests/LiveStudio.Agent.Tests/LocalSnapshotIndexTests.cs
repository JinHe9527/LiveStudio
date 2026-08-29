using LiveStudio.Contracts;
using Microsoft.Data.Sqlite;

namespace LiveStudio.Agent.Tests;

public sealed class LocalSnapshotIndexTests
{
    [Fact]
    public async Task RepeatedInitializationDoesNotFailRunningOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "LiveStudioIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var index = new LocalSnapshotIndex(root);
            await index.InitializeAsync(CancellationToken.None);
            var operation = new LocalOperationRecord(
                Guid.NewGuid(),
                LocalOperationKind.Restore,
                LocalOperationStatus.Running,
                "正在恢复",
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                null);
            await index.SaveOperationAsync(operation, CancellationToken.None);

            await index.InitializeAsync(CancellationToken.None);

            var saved = Assert.Single(await index.GetOperationsAsync(CancellationToken.None));
            Assert.Equal(LocalOperationStatus.Running, saved.Status);
            Assert.Null(saved.CompletedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
