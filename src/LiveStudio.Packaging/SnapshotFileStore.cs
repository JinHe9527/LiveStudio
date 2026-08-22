using System.Security.Cryptography;

namespace LiveStudio.Packaging;

public sealed record SnapshotFileRecord(
    string PackagePath,
    long PackageLength,
    SnapshotPackageInspection Inspection);

public sealed record SnapshotFileStoreLoadResult(
    IReadOnlyList<SnapshotFileRecord> Snapshots,
    IReadOnlyList<string> Errors);

public sealed class SnapshotFileStore(string directoryPath)
{
    private readonly string directoryPath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(directoryPath)
            ? throw new ArgumentException("存档目录不能为空", nameof(directoryPath))
            : directoryPath);

    public async Task<SnapshotFileStoreLoadResult> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new SnapshotFileStoreLoadResult([], []);
        }

        var snapshots = new List<SnapshotFileRecord>();
        var errors = new List<string>();
        foreach (var packagePath in Directory.EnumerateFiles(directoryPath, "*.lscfg")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                snapshots.Add(await InspectFileAsync(packagePath, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or SnapshotPackageException)
            {
                errors.Add($"{Path.GetFileName(packagePath)}：{exception.Message}");
            }
        }

        return new SnapshotFileStoreLoadResult(
            snapshots
                .OrderByDescending(snapshot => snapshot.Inspection.Package.Snapshot.CreatedAt)
                .ToArray(),
            errors);
    }

    public async Task<SnapshotFileRecord> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var source = await InspectFileAsync(sourcePath, cancellationToken);
        Directory.CreateDirectory(directoryPath);
        var destinationPath = GetPackagePath(source.Inspection.Package.Snapshot.Id);
        if (PathsEqual(source.PackagePath, destinationPath))
        {
            return source;
        }

        if (File.Exists(destinationPath))
        {
            if (await FilesMatchAsync(source.PackagePath, destinationPath, cancellationToken))
            {
                return await InspectFileAsync(destinationPath, cancellationToken);
            }

            throw new SnapshotPackageException(
                $"本地存档库中已存在 ID 为 {source.Inspection.Package.Snapshot.Id} 的不同内容");
        }

        var temporaryPath = Path.Combine(
            directoryPath,
            $".{source.Inspection.Package.Snapshot.Id:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(
                             source.PackagePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             131_072,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             131_072,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var copied = await InspectFileAsync(temporaryPath, cancellationToken);
            if (copied.Inspection.Package.Snapshot.Id != source.Inspection.Package.Snapshot.Id)
            {
                throw new SnapshotPackageException("复制后的存档 ID 与源存档不一致");
            }

            File.Move(temporaryPath, destinationPath);
            return copied with { PackagePath = destinationPath };
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<SnapshotFileRecord> GetAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        InspectFileAsync(GetPackagePath(snapshotId), cancellationToken);

    private static async Task<SnapshotFileRecord> InspectFileAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var inspection = await SnapshotPackageReader.InspectAsync(fullPath, cancellationToken);
        return new SnapshotFileRecord(fullPath, new FileInfo(fullPath).Length, inspection);
    }

    private string GetPackagePath(Guid snapshotId) =>
        Path.Combine(directoryPath, $"{snapshotId:N}.lscfg");

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<bool> FilesMatchAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        await using var left = new FileStream(
            leftPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(
            rightPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftHashTask = SHA256.HashDataAsync(left, cancellationToken).AsTask();
        var rightHashTask = SHA256.HashDataAsync(right, cancellationToken).AsTask();
        await Task.WhenAll(leftHashTask, rightHashTask);
        var leftHash = await leftHashTask;
        var rightHash = await rightHashTask;
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
