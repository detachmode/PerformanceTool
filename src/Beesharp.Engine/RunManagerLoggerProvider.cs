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

    /// <summary>Initializes an unbound <see cref="RunManagerLoggerProvider"/>; call <see cref="Attach"/> once the <see cref="RunManager"/> is built.</summary>
    public RunManagerLoggerProvider() { }

    /// <summary>Initializes a <see cref="RunManagerLoggerProvider"/> bound to the given <see cref="RunManager"/>.</summary>
    /// <param name="runs">The run manager to forward host logs into.</param>
    public RunManagerLoggerProvider(RunManager runs) => this.runs = runs;

    /// <summary>Binds the <see cref="RunManager"/> instance after the DI container is built (avoids an <see cref="ILoggerFactory"/> circular dependency).</summary>
    /// <param name="manager">The run manager to attach.</param>
    public void Attach(RunManager manager) => runs = manager;

    /// <summary>Creates an <see cref="ILogger"/> for the specified host logging category.</summary>
    /// <param name="categoryName">The category name of the logger.</param>
    /// <returns>An <see cref="ILogger"/> that forwards enabled messages to the attached <see cref="RunManager"/>.</returns>
    public ILogger CreateLogger(string categoryName) => new HostLogger(this, categoryName);

    /// <summary>Releases resources used by the logger provider.</summary>
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
