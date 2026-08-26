using System.Text.Json;
using System.Security.Cryptography;
using LiveStudio.Core;

namespace LiveStudio.Adapters.LiveCompanion;

public static class LiveCompanionRecovery
{
    public static Task RecoverPendingAsync(CancellationToken cancellationToken) =>
        LiveCompanionTransactionJournal.RecoverPendingAsync(
            new LiveCompanionConfigurationStore(),
            cancellationToken);
}

internal sealed class LiveCompanionTransactionJournal
{
    private const string ManifestFileName = "transaction.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string transactionDirectory;
    private readonly LiveCompanionTransactionManifest manifest;

    private LiveCompanionTransactionJournal(
        string transactionDirectory,
        LiveCompanionTransactionManifest manifest)
    {
        this.transactionDirectory = transactionDirectory;
        this.manifest = manifest;
    }

    public static async Task<LiveCompanionTransactionJournal> CreateAsync(
        Guid transactionId,
        LiveCompanionProcessInfo? originalProcess,
        CancellationToken cancellationToken)
    {
        var transactionDirectory = Path.Combine(RootDirectory, transactionId.ToString("N"));
        Directory.CreateDirectory(transactionDirectory);
        var manifest = new LiveCompanionTransactionManifest(
            transactionId,
            DateTimeOffset.UtcNow,
            originalProcess is not null,
            originalProcess?.ExecutablePath ?? string.Empty,
            "Initialized",
            []);
        var journal = new LiveCompanionTransactionJournal(transactionDirectory, manifest);
        await journal.WriteManifestAsync(manifest, cancellationToken);
        return journal;
    }

    public async Task SaveBackupsAsync(
        IReadOnlyDictionary<string, byte[]> backups,
        CancellationToken cancellationToken)
    {
        var entries = new List<LiveCompanionBackupEntry>();
        var index = 0;
        foreach (var item in backups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var backupFileName = $"{index:D4}.bin";
            await WriteDurableAsync(
                Path.Combine(transactionDirectory, backupFileName),
                item.Value,
                cancellationToken);
            entries.Add(new LiveCompanionBackupEntry(
                item.Key,
                backupFileName,
                Convert.ToHexStringLower(SHA256.HashData(item.Value)),
                item.Value.LongLength));
            index++;
        }

        await WriteManifestAsync(manifest with { Phase = "BackedUp", Backups = entries }, cancellationToken);
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(transactionDirectory))
        {
            Directory.Delete(transactionDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public string GetWorkingPath(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(transactionDirectory, fileName));
        if (!IsWithinRoot(path, transactionDirectory))
        {
            throw new InvalidOperationException("直播伴侣事务工作文件路径无效");
        }

        return path;
    }

    public static async Task RecoverPendingAsync(
        LiveCompanionConfigurationStore configurationStore,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                // No configuration write can begin before CreateAsync has durably written the
                // manifest and returned the session.
                Directory.Delete(directory, recursive: true);
                continue;
            }

            var manifest = JsonSerializer.Deserialize<LiveCompanionTransactionManifest>(
                await File.ReadAllBytesAsync(manifestPath, cancellationToken),
                JsonOptions) ?? throw new InvalidOperationException($"无法解析未完成事务 {manifestPath}");
            if (manifest.TransactionId == Guid.Empty
                || !string.Equals(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(Path.Combine(RootDirectory, manifest.TransactionId.ToString("N"))),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"直播伴侣事务标识或目录无效: {manifestPath}");
            }

            if (await RestoreTransactionJournal.IsCommittedAsync(
                    manifest.TransactionId,
                    cancellationToken))
            {
                Directory.Delete(directory, recursive: true);
                continue;
            }

            var running = LiveCompanionProcessController.FindRunning();
            if (running is not null)
            {
                await LiveCompanionProcessController.StopAsync(running.ProcessId, cancellationToken);
            }

            var backup = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Backups)
            {
                var originalPath = Path.GetFullPath(entry.OriginalPath);
                if (!IsWithinRoot(originalPath, configurationStore.RootPath))
                {
                    throw new InvalidOperationException($"事务备份路径超出直播伴侣配置目录: {originalPath}");
                }

                var backupPath = Path.GetFullPath(Path.Combine(directory, entry.BackupFileName));
                if (!IsWithinRoot(backupPath, directory) || !File.Exists(backupPath))
                {
                    throw new InvalidOperationException($"事务备份文件无效: {entry.BackupFileName}");
                }

                var content = await File.ReadAllBytesAsync(backupPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(entry.Sha256)
                    && (content.LongLength != entry.Length
                        || !string.Equals(
                            Convert.ToHexStringLower(SHA256.HashData(content)),
                            entry.Sha256,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"直播伴侣事务备份校验失败: {entry.BackupFileName}");
                }

                backup.Add(originalPath, content);
            }

            await LiveCompanionConfigurationStore.RestoreBackupAsync(backup, cancellationToken);
            if (manifest.WasRunning && File.Exists(manifest.ExecutablePath))
            {
                await LiveCompanionProcessController.StartAsync(manifest.ExecutablePath, cancellationToken);
                await LiveCompanionProcessController.WaitUntilRunningAsync(cancellationToken);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveStudio",
        "Transactions",
        "LiveCompanion");

    private async Task WriteManifestAsync(
        LiveCompanionTransactionManifest value,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(transactionDirectory, ManifestFileName);
        await WriteDurableAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
            cancellationToken);
    }

    private static async Task WriteDurableAsync(
        string destinationPath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
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
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(
            normalizedRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record LiveCompanionTransactionManifest(
        Guid TransactionId,
        DateTimeOffset CreatedAt,
        bool WasRunning,
        string ExecutablePath,
        string Phase,
        IReadOnlyList<LiveCompanionBackupEntry> Backups);

    private sealed record LiveCompanionBackupEntry(
        string OriginalPath,
        string BackupFileName,
        string? Sha256 = null,
        long Length = 0);
}
