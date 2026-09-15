namespace LiveStudio.Diagnostics;

public static class DiagnosticReportValidation
{
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "CompanionSecondaryContainer", "ConfigurationOrResourceNotFound", "SignatureValidationFailed",
        "RollbackFailure", "ReadbackMismatch", "AdapterCompatibilityFailure", "AccessDenied", "ResourceNotFound",
        "Timeout", "NetworkFailure", "InvalidJson", "CryptographicFailure", "StorageOrPipeFailure", "UnhandledOperationFailure"
        , "CompanionDirectoryLimit", "CompanionMultipleRoots", "CompanionDirectoryLink", "CompanionFileLocked",
        "CompanionTargetFieldsMissing", "CompanionConfigurationChanging", "CompanionPortableProfileUnsupported"
    };

    public static bool IsValid(DiagnosticReport? report) => report is not null && report.SchemaVersion == 1
        && report.ReportId != Guid.Empty && IsHash(report.Fingerprint)
        && report.Component is "Desktop" or "Agent" or "Setup"
        && Symbol(report.Operation, 100) && Version.TryParse(report.Version, out _)
        && Version.TryParse(report.OperatingSystem, out _)
        && report.Architecture is "X64" or "X86" or "Arm64" or "Arm"
        && report.Occurrences is > 0 and <= 1_000_000
        && report.FirstSeen <= report.LastSeen
        && (report.Applications is null || (report.Applications.Length <= 2
            && report.Applications.All(state => state is not null
                && state.Application is "Obs" or "LiveCompanion" && Version.TryParse(state.Version, out _))))
        && report.Exceptions is { Length: > 0 and <= 5 }
        && report.Exceptions.All(item => item is not null && Symbol(item.Type, 150)
            && IsReason(item.Reason) && item.Frames is { Length: <= 16 }
            && item.Frames.All(frame => Symbol(frame, 500, generated: true)));

    private static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => char.IsAsciiHexDigitLower(character));

    private static bool IsReason(string? value) => value is not null && (Reasons.Contains(value)
        || (value.StartsWith("Operation.", StringComparison.Ordinal)
            && Enum.TryParse<DiagnosticFailureCode>(value[10..], out var code) && Enum.IsDefined(code)));

    private static bool Symbol(string? value, int maximum, bool generated = false) => value is not null
        && value.Length > 0 && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_'
            || (generated && character is '<' or '>' or '+' or '`'));
}
