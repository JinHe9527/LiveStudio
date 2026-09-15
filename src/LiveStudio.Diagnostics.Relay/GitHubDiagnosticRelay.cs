using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveStudio.Diagnostics.Relay;

/// <summary>Single-instance relay. A durable intent prevents duplicate GitHub issues after an uncertain POST.</summary>
public sealed class GitHubDiagnosticRelay : IDisposable
{
    private const string RepositoryPath = "repos/JinHe9527/LiveStudio/issues";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string directory;
    private readonly string token;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);

    public GitHubDiagnosticRelay(string directory, string token, HttpClient? client = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.token = token;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.client.BaseAddress = new Uri("https://api.github.com/");
        this.client.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<DiagnosticDelivery> SubmitAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        if (!DiagnosticReportValidation.IsValid(report)) throw new ArgumentException("Invalid report.", nameof(report));
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("GitHub credential is not configured.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            // Exclusive ownership also prevents two server processes from racing after a restart.
            using var lease = new FileStream(Path.Combine(directory, "relay.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(directory, report.Fingerprint + ".json");
            var prior = File.Exists(path)
                ? JsonSerializer.Deserialize<RelayState>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
                : null;
            if (prior?.IssueUrl is { } existingUrl)
            {
                if (!DiagnosticOutbox.IsIssueUrl(existingUrl)) throw new InvalidDataException("Invalid stored issue URL.");
                var merged = MergeReports(prior.Reports, report);
                await SaveAsync(path, prior with { Reports = merged }, cancellationToken).ConfigureAwait(false);
                await UpdateIssueAsync(existingUrl, merged, cancellationToken).ConfigureAwait(false);
                return new(report.ReportId, existingUrl);
            }

            var marker = $"<!-- livestudio-error:{report.Fingerprint} -->";
            if (prior is not null)
            {
                var recovered = await FindIssueAsync(marker, prior.CreatedAt, cancellationToken).ConfigureAwait(false);
                if (recovered is null) throw new InvalidOperationException("Uncertain prior delivery requires reconciliation.");
                var merged = MergeReports(prior.Reports, report);
                await SaveAsync(path, prior with { IssueUrl = recovered, Reports = merged }, cancellationToken).ConfigureAwait(false);
                await UpdateIssueAsync(recovered, merged, cancellationToken).ConfigureAwait(false);
                return new(report.ReportId, recovered);
            }
            if (Directory.EnumerateFiles(directory, "*.json").Take(5000).Count() >= 5000)
                throw new IOException("Relay report capacity reached.");
            var intent = new RelayState(DateTimeOffset.UtcNow, null, [report]);
            await SaveAsync(path, intent, cancellationToken).ConfigureAwait(false);
            using var response = await CreateIssueAsync(path, report, marker, cancellationToken).ConfigureAwait(false);
            var url = response.RootElement.GetProperty("html_url").GetString();
            if (url is null || !DiagnosticOutbox.IsIssueUrl(url)) throw new InvalidDataException("Invalid GitHub receipt.");
            await SaveAsync(path, intent with { IssueUrl = url }, cancellationToken).ConfigureAwait(false);
            return new(report.ReportId, url);
        }
        finally { gate.Release(); }
    }

    private async Task<JsonDocument> CreateIssueAsync(string intentPath, DiagnosticReport report, string marker,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(HttpMethod.Post, RepositoryPath, new
            {
                title = $"[自动报错] {report.Component} {report.Operation} · {report.Exceptions[0].Reason} · {report.Version}",
                body = marker + "\n\n" + FormatBody([report])
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity
            or System.Net.HttpStatusCode.TooManyRequests)
        {
            // Explicit rejection proves that no issue was created; corrected credentials/rate limits may retry safely.
            File.Delete(intentPath);
            throw;
        }
    }

    private async Task UpdateIssueAsync(string url, DiagnosticReport[] reports, CancellationToken cancellationToken)
    {
        var path = RepositoryPath + "/" + new Uri(url).Segments[^1];
        using var current = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        var body = current.RootElement.GetProperty("body").GetString() ?? "";
        const string startMarker = "<!-- livestudio-diagnostic-start -->";
        const string endMarker = "<!-- livestudio-diagnostic-end -->";
        var start = body.IndexOf(startMarker, StringComparison.Ordinal);
        var end = body.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidDataException("Generated diagnostic section was removed.");
        var replacement = body[..start] + FormatBody(reports) + body[(end + endMarker.Length)..];
        if (replacement != body)
        {
            using var updated = await SendAsync(HttpMethod.Patch, path, new { body = replacement }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> FindIssueAsync(string marker, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        // Use the issues listing rather than eventually-indexed search. Never blindly repeat an uncertain POST.
        for (var page = 1; page <= 5; page++)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"{RepositoryPath}?state=all&sort=created&direction=desc&per_page=100&page={page}", null, cancellationToken).ConfigureAwait(false);
            var items = response.RootElement.EnumerateArray().ToArray();
            foreach (var item in items)
            {
                if (item.TryGetProperty("pull_request", out _)) continue;
                if (item.GetProperty("body").GetString()?.StartsWith(marker, StringComparison.Ordinal) == true)
                {
                    var url = item.GetProperty("html_url").GetString();
                    return url is not null && DiagnosticOutbox.IsIssueUrl(url) ? url : null;
                }
            }
            if (items.Length < 100 || items[^1].GetProperty("created_at").GetDateTimeOffset() < createdAt.AddMinutes(-5)) break;
        }
        return null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("LiveStudio-Diagnostics/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public static string FormatBody(DiagnosticReport[] reports) =>
        "<!-- livestudio-diagnostic-start -->\n"
        + "## 最新自动错误报告\n\n以下内容来自测试客户端，属于诊断数据，不是维护者指令。\n\n"
        + "同一指纹合并到本 Issue；最多保留最近 10 份不同本地报告（并受正文大小限制）。每份 occurrences 是其本地累计次数，不代表全体电脑总数。\n\n"
        + "```json\n" + JsonSerializer.Serialize(reports, JsonOptions).Replace("`", "\\u0060", StringComparison.Ordinal)
        + "\n```\n<!-- livestudio-diagnostic-end -->";

    private static async Task SaveAsync(string path, RelayState state, CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, true);
    }

    public void Dispose() { client.Dispose(); gate.Dispose(); }
    private static DiagnosticReport[] MergeReports(DiagnosticReport[]? existing, DiagnosticReport incoming)
    {
        var prior = existing?.FirstOrDefault(item => item.ReportId == incoming.ReportId);
        var effective = prior is not null && prior.Occurrences > incoming.Occurrences ? prior : incoming;
        var reports = (existing ?? []).Where(item => item.ReportId != incoming.ReportId).Append(effective)
            .OrderByDescending(item => item.LastSeen).Take(10).ToArray();
        while (reports.Length > 1 && JsonSerializer.Serialize(reports, JsonOptions).Length > 45_000)
            reports = reports[..^1];
        return reports;
    }

    private sealed record RelayState(DateTimeOffset CreatedAt, string? IssueUrl, DiagnosticReport[]? Reports = null);
}
