namespace LiveStudio.Core;

/// <summary>
/// Serializes operations that read a stable cross-application snapshot or mutate application
/// state. Local UI, cloud jobs and heartbeat publication all share this gate in the Agent.
/// </summary>
public sealed class ApplicationOperationGate : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }

    public void Dispose() => semaphore.Dispose();

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
