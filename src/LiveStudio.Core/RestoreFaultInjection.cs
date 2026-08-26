namespace LiveStudio.Core;

/// <summary>
/// 恢复事务的可验证边界。生产默认实现不注入故障；Windows 真机验收执行端可在
/// 显式确认后选择一个边界抛出异常，用于证明该阶段之后仍能恢复原状态。
/// </summary>
public enum RestoreFaultPoint
{
    SessionsCreated,
    ApplicationsStopped,
    AssetsPrepared,
    SettingsApplied,
    ApplicationsStarted,
    VerificationPassed,
    ApplicationsCommitted,
    DurableCommitRecorded
}

public interface IRestoreFaultInjector
{
    Task InjectAsync(RestoreFaultPoint point, CancellationToken cancellationToken);
}

public sealed class NoOpRestoreFaultInjector : IRestoreFaultInjector
{
    public Task InjectAsync(RestoreFaultPoint point, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
