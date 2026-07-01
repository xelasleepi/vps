using System.IO;
using System.Text;

using Setup.Core.Abstractions;
using Setup.Core.Models;

namespace Setup.Core.Services;

/// <summary>
/// Thread-safe file logger. Every entry is written to the dedicated file for its
/// <see cref="LogCategory"/> and always mirrored to <c>install.log</c> so that
/// file is the full chronological transcript of the deployment. Failures during
/// file IO are swallowed — logging must never crash the application.
/// </summary>
public sealed class Logger : ILogger, IDisposable
{
    private readonly object _sync = new();

    private readonly string _installPath;
    private readonly string _errorsPath;
    private readonly string _downloadsPath;
    private readonly string _optimizationPath;
    private readonly string _softwarePath;

    private readonly StreamWriter? _install;
    private readonly StreamWriter? _errors;
    private readonly StreamWriter? _downloads;
    private readonly StreamWriter? _optimization;
    private readonly StreamWriter? _software;

    private bool _disposed;

    /// <inheritdoc />
    public event Action<LogEntry>? EntryLogged;

    /// <summary>
    /// Creates the logger and opens the five log files under
    /// <see cref="WorkingDirectories.Logs"/> for appending. A session banner is
    /// written to <c>install.log</c> on construction.
    /// </summary>
    /// <param name="dirs">Resolved working directories (the log folder must exist).</param>
    public Logger(WorkingDirectories dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);

        _installPath = Path.Combine(dirs.Logs, "install.log");
        _errorsPath = Path.Combine(dirs.Logs, "errors.log");
        _downloadsPath = Path.Combine(dirs.Logs, "downloads.log");
        _optimizationPath = Path.Combine(dirs.Logs, "optimization.log");
        _softwarePath = Path.Combine(dirs.Logs, "software.log");

        _install = TryOpen(_installPath);
        _errors = TryOpen(_errorsPath);
        _downloads = TryOpen(_downloadsPath);
        _optimization = TryOpen(_optimizationPath);
        _software = TryOpen(_softwarePath);

        WriteSessionHeader();
    }

    /// <summary>Opens a UTF-8 append writer with buffering (AutoFlush off).</summary>
    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        }
        catch
        {
            return null;
        }
    }

    private void WriteSessionHeader()
    {
        try
        {
            var now = DateTime.Now;
            var header =
                "╔══════════════════════════════════════════════════════════════╗" + Environment.NewLine +
                "║  Setup.exe — silent deployment session                       ║" + Environment.NewLine +
                $"║  Started: {now:yyyy-MM-dd HH:mm:ss}                                 ║" + Environment.NewLine +
                "╚══════════════════════════════════════════════════════════════╝";

            lock (_sync)
            {
                if (_install is not null)
                {
                    _install.WriteLine(header);
                    _install.Flush();
                }
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }

    /// <inheritdoc />
    public void Log(LogLevel level, string message, LogCategory category = LogCategory.Install)
    {
        var entry = new LogEntry(DateTime.Now, level, message ?? string.Empty, category);
        WriteEntry(entry, exception: null);
        RaiseEntryLogged(entry);
    }

    /// <inheritdoc />
    public void Info(string message, LogCategory category = LogCategory.Install)
        => Log(LogLevel.Info, message, category);

    /// <inheritdoc />
    public void Success(string message, LogCategory category = LogCategory.Install)
        => Log(LogLevel.Success, message, category);

    /// <inheritdoc />
    public void Warning(string message, LogCategory category = LogCategory.Install)
        => Log(LogLevel.Warning, message, category);

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.Errors)
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Error, message ?? string.Empty, category);
        WriteEntry(entry, exception);
        RaiseEntryLogged(entry);
    }

    /// <inheritdoc />
    public void Download(string message)
        => Log(LogLevel.Download, message, LogCategory.Downloads);

    /// <summary>
    /// Writes an entry to its category file and mirrors it to <c>install.log</c>.
    /// Error entries are additionally written to <c>errors.log</c> (with any
    /// supplied exception detail) and the buffers are flushed immediately.
    /// </summary>
    private void WriteEntry(LogEntry entry, Exception? exception)
    {
        var line = entry.ToFileLine();
        var isError = entry.Level == LogLevel.Error;

        try
        {
            lock (_sync)
            {
                if (_disposed) return;

                // Category-specific file.
                var target = WriterFor(entry.Category);
                WriteLine(target, line);

                // Errors are always captured in errors.log regardless of category.
                if (isError && entry.Category != LogCategory.Errors)
                    WriteLine(_errors, line);

                // Exception detail goes to the errors file.
                if (exception is not null)
                {
                    WriteLine(_errors, "         Exception: " + exception.Message);
                    if (!string.IsNullOrEmpty(exception.StackTrace))
                        WriteLine(_errors, exception.StackTrace);
                }

                // Always mirror to install.log for a single chronological transcript,
                // unless the category already is install (avoid duplicate lines).
                if (entry.Category != LogCategory.Install)
                    WriteLine(_install, line);

                if (isError)
                    FlushAll();
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }

    private StreamWriter? WriterFor(LogCategory category) => category switch
    {
        LogCategory.Install => _install,
        LogCategory.Errors => _errors,
        LogCategory.Downloads => _downloads,
        LogCategory.Optimization => _optimization,
        LogCategory.Software => _software,
        _ => _install
    };

    private static void WriteLine(StreamWriter? writer, string text)
    {
        try
        {
            writer?.WriteLine(text);
        }
        catch
        {
            // Ignore — a single failed line must not break logging.
        }
    }

    private void RaiseEntryLogged(LogEntry entry)
    {
        try
        {
            EntryLogged?.Invoke(entry);
        }
        catch
        {
            // Subscriber faults must not affect logging.
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        try
        {
            lock (_sync)
            {
                FlushAll();
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }

    /// <summary>Flushes all open writers. Caller must hold <see cref="_sync"/>.</summary>
    private void FlushAll()
    {
        SafeFlush(_install);
        SafeFlush(_errors);
        SafeFlush(_downloads);
        SafeFlush(_optimization);
        SafeFlush(_software);
    }

    private static void SafeFlush(StreamWriter? writer)
    {
        try
        {
            writer?.Flush();
        }
        catch
        {
            // Ignore flush failures.
        }
    }

    /// <summary>Flushes and closes all log files.</summary>
    public void Dispose()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                FlushAll();
                _install?.Dispose();
                _errors?.Dispose();
                _downloads?.Dispose();
                _optimization?.Dispose();
                _software?.Dispose();
            }
        }
        catch
        {
            // Never throw from the logger.
        }
    }
}
