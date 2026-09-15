using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveStudio.Diagnostics;

namespace LiveStudio.Core.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LiveStudio-Diagnostics-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Endpoint = new("https://diagnostics.example.test/reports");

    [Fact]
    public void ReportNeverIncludesMessagesDataPathsOrCredentials()
    {
        var exception = new IOException(@"C:\Users\SecretUser\password=secret https://host/?token=secret",
            new InvalidOperationException("Cookie=secret OBS password secret"));
        exception.Data["token"] = "secret";
        var report = DiagnosticReportFactory.Create("Desktop", "CaptureSnapshot", exception);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, report.Exceptions.Length);
        Assert.Equal("StorageOrPipeFailure", report.Exceptions[0].Reason);
    }

    [Fact]
    public void FingerprintIgnoresPrivateMessagesButSeparatesOperations()
    {
        var first = CreateReport();
        var same = DiagnosticReportFactory.Create("Desktop", "Capture", new IOException("another user's path"));
        var different = DiagnosticReportFactory.Create("Desktop", "Restore", new IOException());
        Assert.Equal(first.Fingerprint, same.Fingerprint);
        Assert.NotEqual(first.Fingerprint, different.Fingerprint);
    }

    [Fact]
    public void OutboxSurvivesRestartDeduplicatesAndBoundsDiskUse()
    {
        var outbox = new DiagnosticOutbox(directory);
        outbox.Enqueue(CreateReport());
        outbox.Enqueue(CreateReport());
        var restarted = new DiagnosticOutbox(directory);
        Assert.Equal(1, restarted.GetStatus().Pending);
        for (var index = 0; index < 150; index++)
            restarted.Enqueue(DiagnosticReportFactory.Create("Desktop", "Operation" + index, new IOException()));
        Assert.Equal(DiagnosticOutbox.MaximumReports, Directory.GetFiles(directory, "*.json").Length);
    }

    [Fact]
    public async Task SuccessRequiresMatchingReceiptAndValidatedGitHubIssue()
    {
        var outbox = new DiagnosticOutbox(directory);
        var report = CreateReport();
        outbox.Enqueue(report);
        using var client = Client(_ => Response(new DiagnosticDelivery(report.ReportId,
            "https://github.com/JinHe9527/LiveStudio/issues/123")));
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(0, outbox.GetStatus().Pending);
        Assert.EndsWith("/123", outbox.GetStatus().LastIssueUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example/issues/1")]
    [InlineData("https://github.com.evil.example/JinHe9527/LiveStudio/issues/1")]
    [InlineData("https://github.com/JinHe9527/LiveStudio/issues/not-a-number")]
    public async Task InvalidReceiptKeepsReportPending(string issueUrl)
    {
        var outbox = new DiagnosticOutbox(directory);
        var report = CreateReport();
        outbox.Enqueue(report);
        using var client = Client(_ => Response(new DiagnosticDelivery(report.ReportId, issueUrl)));
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(1, outbox.GetStatus().Pending);
        Assert.Null(outbox.GetStatus().LastIssueUrl);
    }

    [Fact]
    public async Task OfflineFailurePersistsAndBacksOffWithoutNewReports()
    {
        var outbox = new DiagnosticOutbox(directory);
        outbox.Enqueue(CreateReport());
        var attempts = 0;
        using var client = Client(_ => { attempts++; throw new HttpRequestException(); });
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Equal(1, new DiagnosticOutbox(directory).GetStatus().Pending);
    }

    [Fact]
    public async Task DisablePersistsAcrossProcessesAndPreventsRequests()
    {
        var outbox = new DiagnosticOutbox(directory);
        outbox.Enqueue(CreateReport());
        outbox.SetEnabled(false);
        var requests = 0;
        using var client = Client(_ => { requests++; return new(HttpStatusCode.ServiceUnavailable); });
        await new DiagnosticOutbox(directory).FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(0, requests);
        Assert.False(outbox.GetStatus().Enabled);
    }

    [Fact]
    public async Task ErrorArrivingDuringDeliveryIsNotLostByAcknowledgement()
    {
        var outbox = new DiagnosticOutbox(directory);
        var report = CreateReport();
        outbox.Enqueue(report);
        using var client = Client(_ =>
        {
            new DiagnosticOutbox(directory).Enqueue(CreateReport());
            return Response(new DiagnosticDelivery(report.ReportId, "https://github.com/JinHe9527/LiveStudio/issues/123"));
        });
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(1, outbox.GetStatus().Pending);
    }

    [Fact]
    public async Task CorruptFileDoesNotBlockOtherReports()
    {
        var outbox = new DiagnosticOutbox(directory);
        var report = CreateReport();
        outbox.Enqueue(report);
        File.WriteAllText(Path.Combine(directory, "corrupt.json"), "{");
        using var client = Client(_ => Response(new DiagnosticDelivery(report.ReportId,
            "https://github.com/JinHe9527/LiveStudio/issues/123")));
        await outbox.FlushAsync(client, Endpoint, CancellationToken.None);
        Assert.Equal(0, outbox.GetStatus().Pending);
    }

    private static DiagnosticReport CreateReport() => DiagnosticReportFactory.Create("Desktop", "Capture", new IOException());
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private static HttpResponseMessage Response(DiagnosticDelivery receipt) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(receipt) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
