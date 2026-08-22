using LiveStudio.Contracts;

namespace LiveStudio.Packaging;

public sealed record PackageFileEntry(
    string Path,
    string Sha256,
    long Length,
    string MediaType);

public sealed record SnapshotPackageManifest(
    Guid SnapshotId,
    Guid OrganizationId,
    Guid RoomId,
    string Name,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    IReadOnlyList<PackageFileEntry> Files);

public sealed record PackageSignature(
    string Algorithm,
    string KeyId,
    string PublicKeyPem,
    string SignatureBase64);

public sealed record PackageSigner(
    string KeyId,
    string PublicKeyPem,
    string FingerprintSha256);

public sealed record SnapshotPackageInspection(
    SnapshotPackage Package,
    PackageSigner Signer);

public sealed record PackageFile(
    string Path,
    string MediaType,
    ReadOnlyMemory<byte> Content);

public sealed record SnapshotPackage(
    SnapshotPackageManifest Manifest,
    CombinedSnapshot Snapshot,
    IReadOnlyDictionary<string, PackageFile> Files);

public sealed class SnapshotPackageException : Exception
{
    public SnapshotPackageException()
    {
    }

    public SnapshotPackageException(string message)
        : base(message)
    {
    }

    public SnapshotPackageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
