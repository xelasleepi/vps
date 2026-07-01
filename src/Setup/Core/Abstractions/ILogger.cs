using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// Thread-safe logger. Every call writes a timestamped line to the category's
/// dedicated file (and mirrors to install.log), and raises
/// <see cref="EntryLogged"/> so the live UI can render it in color.
/// </summary>
public interface ILogger
{
    /// <summary>Raised on every log entry. Handlers may be invoked from any thread.</summary>
    event Action<LogEntry>? EntryLogged;

    void Log(LogLevel level, string message, LogCategory category = LogCategory.Install);

    void Info(string message, LogCategory category = LogCategory.Install);
    void Success(string message, LogCategory category = LogCategory.Install);
    void Warning(string message, LogCategory category = LogCategory.Install);
    void Error(string message, Exception? exception = null, LogCategory category = LogCategory.Errors);
    void Download(string message);

    /// <summary>Flushes any buffered writes to disk.</summary>
    void Flush();
}
