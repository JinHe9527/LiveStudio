namespace LiveStudio.Packaging;

public sealed record NativeExportReport(
    string Name,
    DateTimeOffset InspectedAt,
    string SourceFileName,
    long SourceLength,
    string SourceSha256,
    IReadOnlyList<NativeExportEntryObservation> Entries,
    IReadOnlyList<string> SensitivePaths);

public sealed record NativeExportEntryObservation(
    string Path,
    string StorageFormat,
    long Length,
    long CompressedLength,
    string Sha256,
    IReadOnlyList<NativeExportFieldObservation> Fields);

public sealed record NativeExportFieldObservation(
    string JsonPointer,
    string ValueType,
    string ValueHash);

public sealed record NativeExportDifference(
    string BeforeName,
    string AfterName,
    DateTimeOffset ComparedAt,
    IReadOnlyList<string> AddedEntries,
    IReadOnlyList<string> RemovedEntries,
    IReadOnlyList<string> ChangedEntries,
    IReadOnlyList<string> AddedFields,
    IReadOnlyList<string> RemovedFields,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> SensitivePaths);
