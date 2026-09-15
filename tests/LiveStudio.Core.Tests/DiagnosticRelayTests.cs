using System.Net;
using System.Net.Http.Json;
using LiveStudio.Diagnostics;
using LiveStudio.Diagnostics.Relay;

namespace LiveStudio.Core.Tests;

public sealed class DiagnosticRelayTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "LiveStudio-Relay-" + Guid.NewGuid().ToString("N"));
    private const string IssueUrl = "https://github.com/JinHe9527/LiveStudio/issues/123";

    [Fact]
    public async Task MissingServerCredentialNeverContactsGitHub()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; return Reply(new { }); }));
        using var relay = new GitHubDiagnosticRelay(directory, "", client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitAsync(Report(), CancellationToken.None));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task RestartAndReplayReuseIssueAndPreserveHumanNotes()
    {
        var posts = 0;
        var patches = 0;
        var first = Report();
        var body = GitHubDiagnosticRelay.FormatBody([first]) + "\nMaintainer notes remain.";
        var handler = new Handler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Post) { posts++; return Reply(new { html_url = IssueUrl }); }
            if (request.Method == HttpMethod.Get) return Reply(new { body });
            patches++;
            using var document = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            body = document.RootElement.GetProperty("body").GetString()!;
            return Reply(new { html_url = IssueUrl });
        });
        using (var relay = new GitHubDiagnosticRelay(directory, "server-only-test-token", new HttpClient(handler, false)))
            await relay.SubmitAsync(first, CancellationToken.None);
        using (var restarted = new GitHubDiagnosticRelay(directory, "server-only-test-token", new HttpClient(handler, false)))
        {
            await restarted.SubmitAsync(first with { Occurrences = 2 }, CancellationToken.None);
            await restarted.SubmitAsync(first, CancellationToken.None); // A delayed retry must not lower the counter.
            await restarted.SubmitAsync(Report(), CancellationToken.None); // Another computer, same fingerprint.
        }
        handler.Dispose();
        Assert.Equal(1, posts);
        Assert.Equal(2, patches);
        Assert.Contains("Maintainer notes remain.", body, StringComparison.Ordinal);
        Assert.Contains("\"occurrences\": 2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("server-only-test-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LostPostResponseIsReconciledWithoutCreatingDuplicate()
    {
        var report = Report();
        var posts = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException("response lost"); }
            if (request.RequestUri!.Query.Length > 0)
                return Reply(new[] { new { body = $"<!-- livestudio-error:{report.Fingerprint} -->", html_url = IssueUrl } });
            return Reply(new { body = GitHubDiagnosticRelay.FormatBody([report]) });
        }));
        using var relay = new GitHubDiagnosticRelay(directory, "test-token", client);
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SubmitAsync(report, CancellationToken.None));
        var receipt = await relay.SubmitAsync(report, CancellationToken.None);
        Assert.Equal(IssueUrl, receipt.IssueUrl);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task UncertainDeliveryNotFoundDoesNotBlindlyPostAgain()
    {
        var posts = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; throw new HttpRequestException(); }
            return Reply(Array.Empty<object>());
        }));
        using var relay = new GitHubDiagnosticRelay(directory, "test-token", client);
        var report = Report();
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SubmitAsync(report, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.SubmitAsync(report, CancellationToken.None));
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task InvalidFingerprintCannotEscapeStorageDirectory()
    {
        using var relay = new GitHubDiagnosticRelay(directory, "test-token");
        await Assert.ThrowsAsync<ArgumentException>(() => relay.SubmitAsync(Report() with
        { Fingerprint = "../../outside" }, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ExplicitRejectionCanRetryAfterTheProblemIsCorrected(HttpStatusCode rejection)
    {
        var posts = 0;
        using var client = new HttpClient(new Handler(_ => ++posts == 1
            ? new HttpResponseMessage(rejection) : Reply(new { html_url = IssueUrl })));
        using var relay = new GitHubDiagnosticRelay(directory, "test-token", client);
        var report = Report();
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SubmitAsync(report, CancellationToken.None));
        var receipt = await relay.SubmitAsync(report, CancellationToken.None);
        Assert.Equal(IssueUrl, receipt.IssueUrl);
        Assert.Equal(2, posts);
    }

    private static DiagnosticReport Report() => DiagnosticReportFactory.Create("Agent", "Capture", new IOException());
    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
