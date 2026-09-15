using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveStudio.Diagnostics;

public sealed record DiagnosticDelivery(Guid ReportId, string IssueUrl);
public sealed record DiagnosticQueueStatus(int Pending, string? LastIssueUrl, bool Enabled);
internal sealed record QueuedDiagnostic(DiagnosticReport Report, int DeliveredOccurrences,
    int Attempts, DateTimeOffset RetryAfter, string? IssueUrl);

/// <summary>Bounded, atomic, process-shared local outbox. Delivery never holds the disk mutex.</summary>
public sealed class DiagnosticOutbox(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory = Path.GetFullPath(directory);
    private int delivering;
    public const int MaximumReports = 100;

    public void Enqueue(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!DiagnosticReportValidation.IsValid(report)) throw new ArgumentException("Invalid diagnostic report.", nameof(report));
        Locked(() =>
        {
            var path = Path.Combine(directory, report.Fingerprint + ".json");
            var existing = Read(path);
            var merged = existing is null ? new QueuedDiagnostic(report, 0, 0, DateTimeOffset.MinValue, null)
                : existing with
                {
                    Report = existing.Report with
                    {
                        Occurrences = Math.Min(existing.Report.Occurrences + 1, 1_000_000),
                        LastSeen = report.LastSeen > existing.Report.LastSeen ? report.LastSeen : existing.Report.LastSeen,
                        Applications = report.Applications
                    }
                };
            Write(path, merged);
            foreach (var stale in new DirectoryInfo(directory).GetFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumReports))
                stale.Delete();
            return true;
        });
    }

    public DiagnosticQueueStatus GetStatus() => Locked(() =>
    {
        var entries = ReadAll();
        return new DiagnosticQueueStatus(entries.Count(item => item.Report.Occurrences > item.DeliveredOccurrences),
            entries.Where(item => item.IssueUrl is not null).OrderByDescending(item => item.Report.LastSeen)
                .FirstOrDefault()?.IssueUrl, !File.Exists(Path.Combine(directory, "disabled")));
    });

    public void SetEnabled(bool enabled) => Locked(() =>
    {
        var path = Path.Combine(directory, "disabled");
        if (enabled) File.Delete(path);
        else File.WriteAllText(path, "Automatic submission disabled by user.");
        return true;
    });

    public async Task FlushAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0)
            throw new ArgumentException("Diagnostics requires an HTTPS relay.", nameof(endpoint));
        if (Interlocked.CompareExchange(ref delivering, 1, 0) != 0) return;
        try
        {
            var entries = Locked(() => ReadAll().Where(item => item.Report.Occurrences > item.DeliveredOccurrences
                && item.RetryAfter <= DateTimeOffset.UtcNow).Take(5).ToArray());
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!GetStatus().Enabled) return;
                DiagnosticDelivery? receipt = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    { Content = JsonContent.Create(entry.Report, options: JsonOptions) };
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        // A relay cannot force an unbounded response allocation.
                        await response.Content.LoadIntoBufferAsync(4096, deadline.Token).ConfigureAwait(false);
                        var candidate = await response.Content.ReadFromJsonAsync<DiagnosticDelivery>(JsonOptions,
                            deadline.Token).ConfigureAwait(false);
                        if (candidate?.ReportId == entry.Report.ReportId && IsIssueUrl(candidate.IssueUrl)) receipt = candidate;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                    or OperationCanceledException or InvalidOperationException)
                {
                    // Keep the original report. A failed report delivery must never generate another report.
                }
                Locked(() =>
                {
                    var path = Path.Combine(directory, entry.Report.Fingerprint + ".json");
                    if (Read(path) is not { } latest || latest.Report.ReportId != entry.Report.ReportId) return false;
                    var attempts = receipt is null ? Math.Min(latest.Attempts + 1, 12) : 0;
                    Write(path, latest with
                    {
                        DeliveredOccurrences = receipt is null ? latest.DeliveredOccurrences : entry.Report.Occurrences,
                        Attempts = attempts,
                        RetryAfter = DateTimeOffset.UtcNow.AddMinutes(receipt is null
                            ? Math.Min(360, Math.Pow(2, attempts)) : 1440),
                        IssueUrl = receipt?.IssueUrl ?? latest.IssueUrl
                    });
                    return true;
                });
            }
        }
        finally { Volatile.Write(ref delivering, 0); }
    }

    public static bool IsIssueUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0
        && uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/JinHe9527/LiveStudio/issues/", StringComparison.Ordinal)
        && long.TryParse(uri.Segments.LastOrDefault(), out var issue) && issue > 0;

    private QueuedDiagnostic[] ReadAll() => Directory.EnumerateFiles(directory, "*.json")
        .Take(MaximumReports + 1).Select(Read).OfType<QueuedDiagnostic>().ToArray();

    private static QueuedDiagnostic? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32_768) return null;
            var entry = JsonSerializer.Deserialize<QueuedDiagnostic>(File.ReadAllText(path), JsonOptions);
            return entry is not null && DiagnosticReportValidation.IsValid(entry.Report)
                && entry.DeliveredOccurrences >= 0 && entry.DeliveredOccurrences <= entry.Report.Occurrences
                ? entry : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    private static void Write(string path, QueuedDiagnostic entry)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, entry, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private T Locked<T>(Func<T> operation)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        using var mutex = new Mutex(false, "LiveStudio.Diagnostics." + key);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Diagnostics queue is busy.");
            Directory.CreateDirectory(directory);
            return operation();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}
