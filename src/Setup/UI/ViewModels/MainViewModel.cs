using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Setup.Core.Abstractions;
using Setup.Core.Models;

namespace Setup.UI.ViewModels;

/// <summary>
/// The single binding surface for <c>MainWindow</c> and the deployment engine's
/// <see cref="IProgressReporter"/> sink. All observable mutations are marshalled
/// onto the UI dispatcher because <see cref="IProgressReporter"/> members and the
/// logger's <see cref="ILogger.EntryLogged"/> event are raised from background
/// threads.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IProgressReporter
{
    /// <summary>Maximum log lines retained in memory (oldest trimmed past this).</summary>
    private const int MaxLogLines = 2000;

    private readonly Dispatcher? _dispatcher;
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _clockTimer;

    private ILogger? _logger;
    private Action<LogEntry>? _logHandler;

    /// <summary>Creates a view-model bound to the current application's dispatcher.</summary>
    public MainViewModel()
        : this(System.Windows.Application.Current?.Dispatcher)
    {
    }

    /// <summary>Creates a view-model bound to a specific dispatcher (e.g. for tests).</summary>
    /// <param name="dispatcher">The UI dispatcher, or null to run inline.</param>
    public MainViewModel(Dispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ============================================================
    //  Bound scalar properties
    // ============================================================

    private string _bannerTitle = "Roblox Server Deployment";
    /// <summary>Inner banner title text.</summary>
    public string BannerTitle
    {
        get => _bannerTitle;
        set => Set(ref _bannerTitle, value);
    }

    private string _phaseText = "Initializing";
    /// <summary>Current high-level phase, shown under the banner.</summary>
    public string PhaseText
    {
        get => _phaseText;
        set => Set(ref _phaseText, value);
    }

    private string _currentTask = "";
    /// <summary>The current task line (bold/white).</summary>
    public string CurrentTask
    {
        get => _currentTask;
        set => Set(ref _currentTask, value);
    }

    private string? _currentFile;
    /// <summary>The file currently being processed (cyan, small); null hides it.</summary>
    public string? CurrentFile
    {
        get => _currentFile;
        set => Set(ref _currentFile, value);
    }

    private double _overallProgress;
    /// <summary>Overall progress 0..100 for the main bar.</summary>
    public double OverallProgress
    {
        get => _overallProgress;
        set => Set(ref _overallProgress, value);
    }

    private string _percentText = "0%";
    /// <summary>Overall progress formatted as a percentage, e.g. "62%".</summary>
    public string PercentText
    {
        get => _percentText;
        set => Set(ref _percentText, value);
    }

    private string _elapsedText = "00:00:00";
    /// <summary>Elapsed wall-clock time "HH:mm:ss".</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        set => Set(ref _elapsedText, value);
    }

    private string _etaText = "";
    /// <summary>Estimated time remaining text (from downloads / overall).</summary>
    public string EtaText
    {
        get => _etaText;
        set => Set(ref _etaText, value);
    }

    private string _downloadText = "";
    /// <summary>Active download line, e.g. "Mem Reduct  4.2 / 8.0 MB".</summary>
    public string DownloadText
    {
        get => _downloadText;
        set => Set(ref _downloadText, value);
    }

    private string _downloadSpeedText = "";
    /// <summary>Active download speed, e.g. "3.4 MB/s".</summary>
    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        set => Set(ref _downloadSpeedText, value);
    }

    private bool _isDownloadVisible;
    /// <summary>Whether the live download line is shown.</summary>
    public bool IsDownloadVisible
    {
        get => _isDownloadVisible;
        set => Set(ref _isDownloadVisible, value);
    }

    private bool _isRunning = true;
    /// <summary>Whether a deployment is in progress.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    private bool _isSummaryVisible;
    /// <summary>Whether the final summary overlay is shown.</summary>
    public bool IsSummaryVisible
    {
        get => _isSummaryVisible;
        set => Set(ref _isSummaryVisible, value);
    }

    // ============================================================
    //  Collections
    // ============================================================

    /// <summary>Live, colored terminal log lines (capped at <see cref="MaxLogLines"/>).</summary>
    public ObservableCollection<TerminalLine> LogLines { get; } = new();

    /// <summary>Checklist rows updated live via <see cref="TrackItem"/>.</summary>
    public ObservableCollection<TrackedItem> TrackedItems { get; } = new();

    // ============================================================
    //  Summary (final screen)
    // ============================================================

    private string _summaryHeadline = "Deployment Complete";
    /// <summary>Headline on the summary card.</summary>
    public string SummaryHeadline
    {
        get => _summaryHeadline;
        set => Set(ref _summaryHeadline, value);
    }

    /// <summary>Installed operations (green).</summary>
    public ObservableCollection<string> SummaryInstalled { get; } = new();

    /// <summary>Skipped operations (yellow).</summary>
    public ObservableCollection<string> SummarySkipped { get; } = new();

    /// <summary>Failed operations (red).</summary>
    public ObservableCollection<string> SummaryFailed { get; } = new();

    /// <summary>Optimization operations (blue).</summary>
    public ObservableCollection<string> SummaryOptimizations { get; } = new();

    private string _summaryElapsedText = "00:00:00";
    /// <summary>Total elapsed time on the summary card.</summary>
    public string SummaryElapsedText
    {
        get => _summaryElapsedText;
        set => Set(ref _summaryElapsedText, value);
    }

    private string _summaryLogPath = "";
    /// <summary>Log directory shown on the summary card.</summary>
    public string SummaryLogPath
    {
        get => _summaryLogPath;
        set => Set(ref _summaryLogPath, value);
    }

    private int _summaryFailedCount;
    /// <summary>Number of failed operations (for conditional styling).</summary>
    public int SummaryFailedCount
    {
        get => _summaryFailedCount;
        set => Set(ref _summaryFailedCount, value);
    }

    // ============================================================
    //  IProgressReporter (called from background threads)
    // ============================================================

    /// <inheritdoc />
    public void SetPhase(DeploymentPhase phase) =>
        Invoke(() => PhaseText = FormatPhase(phase));

    /// <inheritdoc />
    public void SetCurrentTask(string task) =>
        Invoke(() => CurrentTask = task ?? string.Empty);

    /// <inheritdoc />
    public void SetOverallProgress(double percent)
    {
        var clamped = Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100);
        Invoke(() =>
        {
            OverallProgress = clamped;
            PercentText = $"{clamped:0}%";
        });
    }

    /// <inheritdoc />
    public void SetCurrentFile(string? fileName) =>
        Invoke(() => CurrentFile = string.IsNullOrWhiteSpace(fileName) ? null : fileName);

    /// <inheritdoc />
    public void ReportDownload(DownloadProgress progress)
    {
        if (progress is null)
        {
            Invoke(() => IsDownloadVisible = false);
            return;
        }

        // Hide when finished or no file is being fetched.
        var finished = string.IsNullOrWhiteSpace(progress.FileName)
                       || (progress.TotalBytes is > 0 && progress.BytesReceived >= progress.TotalBytes.Value);

        var name = string.IsNullOrWhiteSpace(progress.FileName) ? "" : progress.FileName;
        var received = FormatBytes(progress.BytesReceived);
        var total = progress.TotalBytes is > 0 ? FormatBytes(progress.TotalBytes.Value) : "?";
        var line = $"{name}  {received} / {total}";
        var speed = progress.SpeedBytesPerSecond > 0 ? $"{FormatBytes((long)progress.SpeedBytesPerSecond)}/s" : "";
        var eta = FormatEta(progress.Eta);

        Invoke(() =>
        {
            if (finished)
            {
                IsDownloadVisible = false;
                DownloadSpeedText = "";
                return;
            }

            DownloadText = line;
            DownloadSpeedText = speed;
            EtaText = eta;
            IsDownloadVisible = true;
        });
    }

    /// <inheritdoc />
    public void TrackItem(string key, string displayName, OperationStatus status)
    {
        var stableKey = key ?? string.Empty;
        Invoke(() =>
        {
            foreach (var item in TrackedItems)
            {
                if (item.Key == stableKey)
                {
                    item.Status = status;
                    return;
                }
            }
            TrackedItems.Add(new TrackedItem(stableKey, displayName ?? stableKey, status));
        });
    }

    // ============================================================
    //  Integrator helpers
    // ============================================================

    /// <summary>
    /// Subscribes to the logger so each entry appears as a colored terminal line.
    /// Detaches any previously attached logger. Safe to call once at startup.
    /// </summary>
    public void AttachLogger(ILogger logger)
    {
        if (logger is null) return;

        // Detach a previous subscription if re-attaching.
        if (_logger is not null && _logHandler is not null)
            _logger.EntryLogged -= _logHandler;

        _logger = logger;
        _logHandler = OnEntryLogged;
        _logger.EntryLogged += _logHandler;
    }

    private void OnEntryLogged(LogEntry entry)
    {
        var line = new TerminalLine(entry.Message, entry.Level, entry.Timestamp.ToString("HH:mm:ss"));
        Invoke(() => AddLine(line));
    }

    /// <summary>Appends a line to the log manually.</summary>
    public void AppendLine(string text, LogLevel level = LogLevel.Normal)
    {
        var line = new TerminalLine(text ?? string.Empty, level, DateTime.Now.ToString("HH:mm:ss"));
        Invoke(() => AddLine(line));
    }

    private void AddLine(TerminalLine line)
    {
        LogLines.Add(line);
        // Trim oldest to keep the collection bounded for performance.
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    /// <summary>Starts the elapsed-time clock (1s tick).</summary>
    public void StartClock()
    {
        Invoke(() =>
        {
            if (!_stopwatch.IsRunning)
                _stopwatch.Restart();

            _clockTimer ??= CreateTimer();
            _clockTimer.Start();
            UpdateElapsed();
        });
    }

    /// <summary>Stops the elapsed-time clock.</summary>
    public void StopClock()
    {
        Invoke(() =>
        {
            _clockTimer?.Stop();
            _stopwatch.Stop();
            UpdateElapsed();
        });
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = _dispatcher is not null
            ? new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            : new DispatcherTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => UpdateElapsed();
        return timer;
    }

    private void UpdateElapsed() => ElapsedText = FormatTimeSpan(_stopwatch.Elapsed);

    /// <summary>
    /// Fills the summary card from a finished <see cref="DeploymentSummary"/>,
    /// reveals the overlay, marks the run finished and stops the clock.
    /// </summary>
    public void ShowSummary(DeploymentSummary summary)
    {
        if (summary is null) return;

        Invoke(() =>
        {
            SummaryInstalled.Clear();
            SummarySkipped.Clear();
            SummaryFailed.Clear();
            SummaryOptimizations.Clear();

            foreach (var r in summary.Installed) SummaryInstalled.Add(r.StatusLine());
            foreach (var r in summary.Skipped) SummarySkipped.Add(r.StatusLine());
            foreach (var r in summary.Failed) SummaryFailed.Add(r.StatusLine());
            foreach (var r in summary.Optimizations) SummaryOptimizations.Add(r.StatusLine());

            SummaryFailedCount = summary.Failed.Count;
            SummaryHeadline = summary.Failed.Count > 0
                ? $"Deployment Complete — {summary.Failed.Count} failed"
                : "Deployment Complete";
            SummaryElapsedText = FormatTimeSpan(summary.TotalElapsed);
            SummaryLogPath = summary.LogDirectory ?? "";

            OverallProgress = 100;
            PercentText = "100%";
            IsDownloadVisible = false;
            IsSummaryVisible = true;
            IsRunning = false;
        });

        StopClock();
    }

    /// <summary>Reports a fatal startup error: red line + headline.</summary>
    public void SetFatalError(string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "A fatal error occurred." : message;
        Invoke(() =>
        {
            AddLine(new TerminalLine(text, LogLevel.Error, DateTime.Now.ToString("HH:mm:ss")));
            PhaseText = "Failed";
            SummaryHeadline = "Deployment Failed";
            CurrentTask = text;
            IsRunning = false;
        });
        StopClock();
    }

    // ============================================================
    //  Formatting helpers
    // ============================================================

    private static string FormatPhase(DeploymentPhase phase) => phase switch
    {
        DeploymentPhase.Initializing => "Initializing",
        DeploymentPhase.Optimizing => "Optimizing Windows",
        DeploymentPhase.Installing => "Installing Software",
        DeploymentPhase.Configuring => "Configuring",
        DeploymentPhase.CleaningUp => "Cleaning Up",
        DeploymentPhase.Complete => "Complete",
        _ => phase.ToString()
    };

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta is null) return "";
        var t = eta.Value;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Human-readable byte size, e.g. 4404019 → "4.2 MB".</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        // Whole bytes / KB show no decimal noise; MB+ shows one decimal.
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    // ============================================================
    //  Dispatcher marshalling + INotifyPropertyChanged
    // ============================================================

    private void Invoke(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.BeginInvoke(action);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ============================================================
    //  Design-time sample data
    // ============================================================

    private static MainViewModel? _designInstance;

    /// <summary>Sample-populated instance for the XAML designer.</summary>
    public static MainViewModel DesignInstance
    {
        get
        {
            if (_designInstance is not null) return _designInstance;

            var vm = new MainViewModel((Dispatcher?)null)
            {
                PhaseText = "Installing Software",
                CurrentTask = "Installing Visual C++ 2015-2022 x64…",
                CurrentFile = "vc_redist.x64.exe",
                OverallProgress = 62,
                PercentText = "62%",
                ElapsedText = "00:01:23",
                EtaText = "00:00:40",
                DownloadText = "Mem Reduct  4.2 / 8.0 MB",
                DownloadSpeedText = "3.4 MB/s",
                IsDownloadVisible = true,
                IsRunning = true
            };

            vm.TrackedItems.Add(new TrackedItem("opt", "Windows optimizations", OperationStatus.Success));
            vm.TrackedItems.Add(new TrackedItem("vcr", "Visual C++ Redistributables", OperationStatus.InProgress));
            vm.TrackedItems.Add(new TrackedItem("winrar", "WinRAR", OperationStatus.Pending));
            vm.TrackedItems.Add(new TrackedItem("chrome", "Google Chrome", OperationStatus.Skipped));
            vm.TrackedItems.Add(new TrackedItem("mem", "Mem Reduct", OperationStatus.Failed));

            vm.LogLines.Add(new TerminalLine("Roblox Server Deployment starting…", LogLevel.Info, "12:00:01"));
            vm.LogLines.Add(new TerminalLine("Applied 14 Windows optimizations", LogLevel.Success, "12:00:44"));
            vm.LogLines.Add(new TerminalLine("Downloading vc_redist.x64.exe", LogLevel.Download, "12:01:02"));
            vm.LogLines.Add(new TerminalLine("Chrome already installed — skipping", LogLevel.Warning, "12:01:10"));
            vm.LogLines.Add(new TerminalLine("Mem Reduct install failed: timeout", LogLevel.Error, "12:01:23"));

            vm.SummaryInstalled.Add("Visual C++ 2015-2022 x64: SUCCESS (12.4s)");
            vm.SummarySkipped.Add("Google Chrome: SKIPPED (0.1s) — already installed");
            vm.SummaryFailed.Add("Mem Reduct: FAILED (30.0s) — timeout");
            vm.SummaryOptimizations.Add("Disable telemetry: SUCCESS (0.3s)");
            vm.SummaryElapsedText = "00:03:12";
            vm.SummaryLogPath = @"C:\RobloxDeploy\logs";
            vm.SummaryFailedCount = 1;
            vm.SummaryHeadline = "Deployment Complete — 1 failed";

            _designInstance = vm;
            return vm;
        }
    }
}
