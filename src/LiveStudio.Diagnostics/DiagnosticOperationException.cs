namespace LiveStudio.Diagnostics;

public enum DiagnosticFailureCode
{
    AgentUnavailable, AgentResponseTimeout, DeviceOffline, MappingRequired, UnsupportedDeviceMode,
    MissingFilter, MissingAsset, IncompatibleVersion, FailedRolledBack, RollbackFailed, UnsupportedMethod
}

public sealed class DiagnosticOperationException(DiagnosticFailureCode code) : Exception(code.ToString())
{
    public DiagnosticFailureCode Code { get; } = code;
}
