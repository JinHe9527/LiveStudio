using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiveStudio.Diagnostics;

/// <summary>Process entry point; disabled until explicitly started so tests/demo do not transmit.</summary>
public static class ErrorDiagnostics
{
    private static DiagnosticOutbox? queue;
    private static string component = "Unknown";
    private static Uri? endpoint;
    private static DiagnosticApplicationState[] applications = [];
    public static bool HasSubmissionEndpoint => endpoint is not null;

    public static DiagnosticPackageContent CreatePackage(DiagnosticCompatibilitySummary? compatibility = null) =>
        DiagnosticPackage.Create(queue ?? new DiagnosticOutbox(DirectoryPath), Volatile.Read(ref applications), compatibility);
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "LiveStudio", "Diagnostics");

    public static void Start(string processComponent)
    {
        if (queue is not null) return;
        component = processComponent;
        queue = new DiagnosticOutbox(DirectoryPath);
        var configured = typeof(ErrorDiagnostics).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "LiveStudioDiagnosticsEndpoint")?.Value;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
            endpoint = uri;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Record(exception, "UnhandledException");
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Record(args.Exception, "UnobservedTaskException");
        _ = Task.Run(DeliverContinuouslyAsync);
    }

    public static void Record(Exception exception, [CallerMemberName] string operation = "Unknown")
    {
        try
        {
            queue?.Enqueue(DiagnosticReportFactory.Create(component, operation, exception)
            with
            { Applications = Volatile.Read(ref applications) });
        }
        catch (Exception) { /* Diagnostics must not replace an operation failure or break rollback. */ }
    }

    public static void UpdateApplications(IEnumerable<DiagnosticApplicationState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        Volatile.Write(ref applications, states.Where(state => state.Application is "Obs" or "LiveCompanion")
            .Select(state => state with
            {
                Version = Version.TryParse(state.Version, out var version)
                ? version.ToString() : "0.0"
            }).Take(2).ToArray());
    }

    public static void RecordOperationFailure(string operation, string? errorCode)
    {
        if (Enum.TryParse<DiagnosticFailureCode>(errorCode, out var code) && Enum.IsDefined(code))
            Record(new DiagnosticOperationException(code), operation);
    }

    public static string Status
    {
        get
        {
            try
            {
                var status = queue?.GetStatus();
                if (status is null) return "当前会话未启用错误诊断。";
                if (endpoint is null) return "错误自动记录在本机。遇到问题后，导出诊断包通过 QQ 或微信发给开发者。";
                if (!status.Enabled) return $"自动提交已关闭；本机待提交 {status.Pending} 类错误。";
                return $"自动提交到 JinHe9527/LiveStudio Issues；待提交 {status.Pending} 类错误。";
            }
            catch (Exception) { return "无法读取本机错误日志目录。"; }
        }
    }

    public static bool Enabled
    {
        get { try { return queue?.GetStatus().Enabled ?? false; } catch (Exception) { return false; } }
        set { try { queue?.SetEnabled(value); } catch (Exception) { /* The UI status remains authoritative. */ } }
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (queue is null || endpoint is null) return;
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            await queue.FlushAsync(client, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) { /* Persisted reports will be retried later. */ }
    }

    private static async Task DeliverContinuouslyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do { await FlushAsync().ConfigureAwait(false); }
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
    }
}
