namespace Setup.Core.Models;

/// <summary>
/// A single immutable log record. Produced by <see cref="Abstractions.ILogger"/>
/// implementations and consumed by both the file writers and the live UI.
/// </summary>
/// <param name="Timestamp">When the entry was created (local time).</param>
/// <param name="Level">Severity, controls color in the UI.</param>
/// <param name="Message">The human-readable text (no timestamp / tag prefix).</param>
/// <param name="Category">Which log channel/file the entry belongs to.</param>
public readonly record struct LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    string Message,
    LogCategory Category)
{
    /// <summary>The bracketed tag shown before the message, e.g. <c>[SUCCESS]</c>.</summary>
    public string Tag => Level switch
    {
        LogLevel.Success => "SUCCESS",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Download => "DOWNLOAD",
        _ => "LOG"
    };

    /// <summary>Formats the entry for file output: <c>[HH:mm:ss] [TAG] message</c>.</summary>
    public string ToFileLine() => $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Tag}] {Message}";
}
