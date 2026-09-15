using LiveStudio.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LiveStudio.Agent;

/// <summary>Does not format logging state, which may contain credentials or native configuration.</summary>
internal sealed class DiagnosticLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger();
    public void Dispose() { }

    private sealed class DiagnosticLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Request failures are recorded separately with their actual operation.
            if (IsEnabled(logLevel) && eventId.Id != 1203)
                ErrorDiagnostics.Record(exception ?? new InvalidOperationException(), $"AgentEvent{eventId.Id}");
        }
    }
}
