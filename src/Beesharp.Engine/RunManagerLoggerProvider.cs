using Microsoft.Extensions.Logging;

namespace Beesharp.Services;

/// <summary>
/// Forwards host and background-service <see cref="ILogger"/> entries into the active run on <see cref="RunManager"/>
/// so they appear in the Logs UI and are stored for export.
/// </summary>
public sealed class RunManagerLoggerProvider : ILoggerProvider
{
    [ThreadStatic]
    private static bool inLog;

    private volatile RunManager? runs;

    public RunManagerLoggerProvider() { }

    public RunManagerLoggerProvider(RunManager runs) => this.runs = runs;

    /// <summary>Binds the <see cref="RunManager"/> instance after the DI container is built (avoids an <see cref="ILoggerFactory"/> circular dependency).</summary>
    public void Attach(RunManager manager) => runs = manager;

    public ILogger CreateLogger(string categoryName) => new HostLogger(this, categoryName);

    public void Dispose() { }

    private sealed class HostLogger(RunManagerLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            owner.runs is not null
            && logLevel != LogLevel.None
            && !category.StartsWith("Beesharp.Services.Storage", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (owner.runs is not { } manager || !IsEnabled(logLevel) || inLog) return;
            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "Trace",
                LogLevel.Debug => "Debug",
                LogLevel.Information => "Information",
                LogLevel.Warning => "Warning",
                LogLevel.Error or LogLevel.Critical => "Error",
                _ => "Information",
            };

            inLog = true;
            try
            {
                manager.RecordHostLog(level, category, message, exception?.ToString());
            }
            finally
            {
                inLog = false;
            }
        }
    }
}
