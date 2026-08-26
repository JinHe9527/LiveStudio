using System.Text.Json;

namespace LiveStudio.Core;

/// <summary>
/// Records the single durable commit decision shared by every application in a restore job.
/// Application-specific journals remain rollback-capable until this journal reaches Committed.
/// </summary>
public static class RestoreTransactionJournal
{
    private const string ManifestFileName = "transaction.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task PrepareAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var directory = GetTransactionDirectory(transactionId);
        Directory.CreateDirectory(directory);
        await WriteManifestAsync(
            directory,
            new RestoreTransactionManifest(transactionId, DateTimeOffset.UtcNow, "Prepared"),
            cancellationToken);
    }

    public static async Task MarkCommittedAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var directory = GetTransactionDirectory(transactionId);
        var manifest = await ReadManifestAsync(directory, cancellationToken)
            ?? throw new InvalidOperationException($"找不到联合恢复事务 {transactionId:N}");
        await WriteManifestAsync(directory, manifest with { Phase = "Committed" }, cancellationToken);
    }

    public static async Task<bool> IsCommittedAsync(
        Guid transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await ReadManifestAsync(GetTransactionDirectory(transactionId), cancellationToken);
            return string.Equals(manifest?.Phase, "Committed", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            // Keeping the target state is allowed only when a valid durable Committed marker can
            // be proven. A missing, torn or corrupt global decision therefore means rollback.
            return false;
        }
    }

    public static Task CompleteAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetTransactionDirectory(transactionId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static Task CompleteAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "Transactions",
        "Restore");

    private static string GetTransactionDirectory(Guid transactionId) =>
        Path.Combine(RootDirectory, transactionId.ToString("N"));

    private static async Task<RestoreTransactionManifest?> ReadManifestAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<RestoreTransactionManifest>(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken),
            JsonOptions) ?? throw new InvalidOperationException($"无法解析联合恢复事务 {manifestPath}");
        if (manifest.TransactionId == Guid.Empty
            || !string.Equals(directory, GetTransactionDirectory(manifest.TransactionId),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || manifest.Phase is not ("Prepared" or "Committed"))
        {
            throw new InvalidOperationException($"联合恢复事务清单无效: {manifestPath}");
        }

        return manifest;
    }

    private static async Task WriteManifestAsync(
        string directory,
        RestoreTransactionManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RestoreTransactionManifest(
        Guid TransactionId,
        DateTimeOffset CreatedAt,
        string Phase);
}
