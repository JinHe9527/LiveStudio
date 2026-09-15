using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LiveStudio.Diagnostics;
using LiveStudio.Diagnostics.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("reports", _ =>
        RateLimitPartition.GetFixedWindowLimiter("all", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 60, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(_ => new GitHubDiagnosticRelay(
    builder.Configuration["Diagnostics:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "reports"),
    builder.Configuration["Diagnostics:GitHubToken"] ?? ""));
var app = builder.Build();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { service = "LiveStudio.Diagnostics.Relay" }));
app.MapPost("/reports", async (DiagnosticReport report, GitHubDiagnosticRelay relay,
    ILogger<GitHubDiagnosticRelay> logger, CancellationToken cancellationToken) =>
{
    if (!DiagnosticReportValidation.IsValid(report)) return Results.BadRequest(new { error = "InvalidReport" });
    try { return Results.Ok(await relay.SubmitAsync(report, cancellationToken)); }
    catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException
        or InvalidOperationException or OperationCanceledException)
    {
        // Do not leak the GitHub response, token, or filesystem details to an anonymous caller.
        RelayLog.DeliveryFailed(logger, exception.GetType().Name,
            exception is HttpRequestException { StatusCode: { } status } ? (int)status : 0);
        return Results.Json(new { error = "DeliveryPending" }, statusCode: 503);
    }
}).RequireRateLimiting("reports");
app.Run();

internal static partial class RelayLog
{
    [LoggerMessage(1001, LogLevel.Warning, "Diagnostic delivery failed: {FailureType}; HTTP {StatusCode}")]
    public static partial void DeliveryFailed(ILogger logger, string failureType, int statusCode);
}
