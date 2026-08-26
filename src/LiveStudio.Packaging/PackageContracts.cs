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
    IReadOnlyList<SnapshotApplicationManifest> Applications,
    IReadOnlyList<AssetBinding> AssetBindings,
    IReadOnlyList<PackageFileEntry> Files,
    IReadOnlyList<CameraStationSnapshot>? CameraStations = null);

public sealed record SnapshotApplicationManifest(
    ApplicationKind Application,
    string AdapterId,
    string AdapterDefinitionSha256,
    string StructureFingerprint,
    CompatibilityLevel Compatibility,
    bool WasRunning,
    IReadOnlyList<string> FieldCoverage,
    int FieldCoverageCount = 0,
    string FieldCoverageSha256 = "");

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

public class SnapshotPackageException : Exception
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

public sealed class SnapshotSensitiveDataException : SnapshotPackageException
{
    public SnapshotSensitiveDataException(string message)
        : base(message)
    {
    }

    public SnapshotSensitiveDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
