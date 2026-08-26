using LiveStudio.Core;

namespace LiveStudio.Agent;

/// <summary>
/// 仅供隔离 Windows 验收机使用的显式故障注入器。没有双重确认时保持关闭；
/// 指定了故障点却缺少确认值时 Agent 拒绝启动，避免生产环境误注入。
/// </summary>
internal sealed class EnvironmentRestoreFaultInjector : IRestoreFaultInjector
{
    internal const string ConfirmationVariable = "LIVESTUDIO_ENABLE_DESTRUCTIVE_VALIDATION";
    internal const string FaultPointVariable = "LIVESTUDIO_VALIDATION_FAULT_POINT";
    internal const string BehaviorVariable = "LIVESTUDIO_VALIDATION_FAULT_BEHAVIOR";
    internal const string CrashConfirmationVariable = "LIVESTUDIO_ENABLE_PROCESS_CRASH_VALIDATION";
    internal const string RequiredConfirmation = "I_UNDERSTAND_THIS_IS_DESTRUCTIVE";
    internal const string RequiredCrashConfirmation = "I_ACCEPT_IMMEDIATE_AGENT_TERMINATION";

    private readonly RestoreFaultPoint? faultPoint;
    private readonly bool crashProcess;

    internal EnvironmentRestoreFaultInjector(
        string? confirmation,
        string? configuredFaultPoint,
        string? behavior = null,
        string? crashConfirmation = null)
    {
        if (string.IsNullOrWhiteSpace(configuredFaultPoint))
        {
            return;
        }

        if (!string.Equals(confirmation, RequiredConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"设置 {FaultPointVariable} 时必须同时设置 {ConfirmationVariable}={RequiredConfirmation}");
        }

        if (!Enum.TryParse<RestoreFaultPoint>(configuredFaultPoint, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException(
                $"未知恢复故障点 {configuredFaultPoint}；允许值：{string.Join(", ", Enum.GetNames<RestoreFaultPoint>())}");
        }

        faultPoint = parsed;
        if (string.IsNullOrWhiteSpace(behavior)
            || string.Equals(behavior, "Throw", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(behavior, "Crash", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"未知故障行为 {behavior}；允许值：Throw, Crash");
        }

        if (!string.Equals(crashConfirmation, RequiredCrashConfirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"设置 {BehaviorVariable}=Crash 时必须同时设置 "
                + $"{CrashConfirmationVariable}={RequiredCrashConfirmation}");
        }

        crashProcess = true;
    }

    public Task InjectAsync(RestoreFaultPoint point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (faultPoint != point)
        {
            return Task.CompletedTask;
        }

        if (crashProcess)
        {
            Environment.FailFast($"Windows 真机验收强制终止 Agent：{point}");
        }

        return Task.FromException(new InvalidOperationException($"Windows 真机验收注入故障：{point}"));
    }

    internal static EnvironmentRestoreFaultInjector FromEnvironment() => new(
        Environment.GetEnvironmentVariable(ConfirmationVariable),
        Environment.GetEnvironmentVariable(FaultPointVariable),
        Environment.GetEnvironmentVariable(BehaviorVariable),
        Environment.GetEnvironmentVariable(CrashConfirmationVariable));
}
