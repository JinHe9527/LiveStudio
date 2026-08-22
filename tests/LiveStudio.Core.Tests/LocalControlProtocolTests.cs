using LiveStudio.Contracts;

namespace LiveStudio.Core.Tests;

public sealed class LocalControlProtocolTests
{
    [Fact]
    public async Task RequestRoundTripsThroughLengthPrefixedProtocol()
    {
        var expected = LocalControlProtocol.CreateRequest(
            LocalControlMethod.CaptureSnapshot,
            new CaptureLocalSnapshotRequest("午间直播"));
        await using var stream = new MemoryStream();

        await LocalControlProtocol.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await LocalControlProtocol.ReadAsync<LocalControlRequest>(stream, CancellationToken.None);
        var payload = LocalControlProtocol.DeserializePayload<CaptureLocalSnapshotRequest>(actual.Payload);

        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(LocalControlMethod.CaptureSnapshot, actual.Method);
        Assert.Equal("午间直播", payload.Name);
    }

    [Fact]
    public void FailureResponsePreservesAgentErrorCode()
    {
        var response = LocalControlProtocol.CreateFailure(
            Guid.NewGuid(),
            "BlockedByLiveSession",
            "开播期间禁止恢复");

        var exception = Assert.Throws<LocalControlException>(() =>
            LocalControlProtocol.DeserializeResult<LocalAgentState>(response));

        Assert.Equal("BlockedByLiveSession", exception.ErrorCode);
        Assert.Equal("开播期间禁止恢复", exception.Message);
    }

    [Fact]
    public async Task DeviceEnrollmentRequestRoundTripsWithoutLosingServiceUri()
    {
        var expected = LocalControlProtocol.CreateRequest(
            LocalControlMethod.EnrollDevice,
            new EnrollLocalDeviceRequest(
                new Uri("https://studio.example/"),
                new string('a', 43),
                "直播电脑 A"));
        await using var stream = new MemoryStream();

        await LocalControlProtocol.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await LocalControlProtocol.ReadAsync<LocalControlRequest>(stream, CancellationToken.None);
        var payload = LocalControlProtocol.DeserializePayload<EnrollLocalDeviceRequest>(actual.Payload);

        Assert.Equal(LocalControlMethod.EnrollDevice, actual.Method);
        Assert.Equal(new Uri("https://studio.example/"), payload.ServiceUri);
        Assert.Equal("直播电脑 A", payload.DeviceName);
    }

    [Fact]
    public void LocalOperationHistoryRoundTripsWithRollbackResult()
    {
        var operation = new LocalOperationSummary(
            Guid.NewGuid(),
            LocalOperationKind.Restore,
            LocalOperationStatus.FailedRolledBack,
            "验证失败，已回滚",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(3));
        var state = new LocalAgentState(
            "STUDIO-A",
            true,
            false,
            true,
            false,
            true,
            "恢复失败，已还原原状态",
            null,
            "未配置",
            [],
            [],
            [operation]);
        var response = LocalControlProtocol.CreateSuccess(Guid.NewGuid(), state);

        var restored = LocalControlProtocol.DeserializeResult<LocalAgentState>(response);

        var restoredOperation = Assert.Single(restored.Operations);
        Assert.Equal(LocalOperationStatus.FailedRolledBack, restoredOperation.Status);
        Assert.Equal(operation.SnapshotId, restoredOperation.SnapshotId);
        Assert.Equal("验证失败，已回滚", restoredOperation.Message);
    }

    [Fact]
    public async Task SnapshotDetailRequestPreservesSnapshotId()
    {
        var snapshotId = Guid.NewGuid();
        var expected = LocalControlProtocol.CreateRequest(
            LocalControlMethod.GetSnapshotDetail,
            new GetLocalSnapshotDetailRequest(snapshotId));
        await using var stream = new MemoryStream();

        await LocalControlProtocol.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await LocalControlProtocol.ReadAsync<LocalControlRequest>(stream, CancellationToken.None);
        var payload = LocalControlProtocol.DeserializePayload<GetLocalSnapshotDetailRequest>(actual.Payload);

        Assert.Equal(LocalControlMethod.GetSnapshotDetail, actual.Method);
        Assert.Equal(snapshotId, payload.SnapshotId);
    }
}
