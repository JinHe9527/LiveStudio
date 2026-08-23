namespace LiveStudio.Discovery.Windows;

public sealed record DiscoveryReport(
    string Name,
    DateTimeOffset CapturedAt,
    string MachineName,
    string OperatingSystem,
    IReadOnlyList<ProcessObservation> Processes,
    IReadOnlyList<FileObservation> Files,
    IReadOnlyList<RegistryObservation> RegistryValues);

public sealed record ProcessObservation(
    string Name,
    int ProcessId,
    string? ExecutablePath,
    string? ProductVersion,
    DateTimeOffset? StartTime);

public sealed record FileObservation(
    string Root,
    string RelativePath,
    string StorageFormat,
    long Length,
    DateTimeOffset LastWriteTime,
    string Sha256);

public sealed record RegistryObservation(
    string KeyPath,
    string ValueName,
    string ValueKind,
    string ValueHash);

public sealed record DiscoveryDifference(
    string BeforeName,
    string AfterName,
    DateTimeOffset ComparedAt,
    IReadOnlyList<string> AddedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> AddedRegistryValues,
    IReadOnlyList<string> RemovedRegistryValues,
    IReadOnlyList<string> ChangedRegistryValues);
