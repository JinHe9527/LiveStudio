using LiveStudio.Core;

namespace LiveStudio.Core.Tests;

public sealed class RestoreTransactionJournalTests
{
    [Fact]
    public async Task PreparedTransactionBecomesCommittedOnlyAfterDurableDecision()
    {
        var transactionId = Guid.NewGuid();
        try
        {
            await RestoreTransactionJournal.PrepareAsync(transactionId, CancellationToken.None);
            Assert.False(await RestoreTransactionJournal.IsCommittedAsync(
                transactionId,
                CancellationToken.None));

            await RestoreTransactionJournal.MarkCommittedAsync(transactionId, CancellationToken.None);
            Assert.True(await RestoreTransactionJournal.IsCommittedAsync(
                transactionId,
                CancellationToken.None));
        }
        finally
        {
            await RestoreTransactionJournal.CompleteAsync(transactionId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CorruptManifestIsNeverTreatedAsCommitted()
    {
        var transactionId = Guid.NewGuid();
        var directory = GetDirectory(transactionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "transaction.json"),
            "{not-json",
            CancellationToken.None);
        try
        {
            Assert.False(await RestoreTransactionJournal.IsCommittedAsync(
                transactionId,
                CancellationToken.None));
        }
        finally
        {
            await RestoreTransactionJournal.CompleteAsync(transactionId, CancellationToken.None);
        }
    }

    private static string GetDirectory(Guid transactionId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "Transactions",
        "Restore",
        transactionId.ToString("N"));
}
