using LiveStudio.Core;

namespace LiveStudio.Core.Tests;

public sealed class ApplicationOperationGateTests
{
    [Fact]
    public async Task SecondApplicationOperationWaitsForFirstLease()
    {
        using var gate = new ApplicationOperationGate();
        using var first = await gate.EnterAsync(CancellationToken.None);

        var secondTask = gate.EnterAsync(CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask;
        Assert.True(secondTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitingApplicationOperationHonorsCancellation()
    {
        using var gate = new ApplicationOperationGate();
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await gate.EnterAsync(cancellation.Token));
    }
}
