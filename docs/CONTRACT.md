# Setup.exe — Build Contract (READ FIRST)

This document is the **binding contract** for all agents building `Setup.exe`.
Do not change files outside your assigned area. Implement **against these exact
signatures** so the parts link together. The scaffolding, models and interfaces
already exist under `src/Setup/Core/` — read them, do not modify them.

## Project facts
- Single WPF project at `src/Setup/Setup.csproj`, `AssemblyName=Setup` → `Setup.exe`.
- TFM `net8.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`, x64.
- SDK-style project: **all `*.cs` and `*.xaml` under the project are auto-included.**
  Just add files in the right folders — do **not** edit `Setup.csproj` unless you
  must add a `<PackageReference>` (avoid new packages if possible).
- Requires Administrator (manifest already set). Everything runs silent/unattended.
- Root namespace `Setup`. Folder → namespace mapping is standard
  (`Core/Services/Foo.cs` → `Setup.Core.Services`).

## Coding standards
- Idempotent + safe to re-run. **Never throw across a public boundary** — installers,
  optimization tasks, the download manager and process runner return result objects
  or booleans; catch and convert exceptions.
- XML `///` doc comments on public types/members. Concise, professional.
- No `MessageBox`, no console prompts, no extra windows. UI is the only surface.
- Use `async`/`await`; accept and honor `CancellationToken` where provided.

## Existing models (namespace `Setup.Core.Models`)
- `LogLevel { Normal, Info, Success, Warning, Error, Download }`
- `LogCategory { Install, Errors, Downloads, Optimization, Software }`
- `OperationStatus { Pending, InProgress, Success, Failed, Skipped }`
- `DeploymentPhase { Initializing, Optimizing, Installing, Configuring, CleaningUp, Complete }`
- `readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Message, LogCategory Category)`
  with `.Tag` and `.ToFileLine()`.
- `OperationResult` — `Name, Status, Message?, Elapsed, Error?`; factories
  `OperationResult.Success/Failed/Skipped(name, elapsed, message?)`; `.StatusLine()`.
- `DownloadRequest` (Url, DestinationPath, DisplayName, Sha256?, MaxRetries,
  TimeoutSeconds, ResumeIfPossible, UserAgent?), `DownloadProgress`
  (FileName, BytesReceived, TotalBytes?, SpeedBytesPerSecond, Elapsed, `.Percent`, `.Eta`),
  `DownloadResult` (Success, FilePath, ErrorMessage?, Attempts, Elapsed, BytesDownloaded,
  ComputedSha256?, HashVerified) with `DownloadResult.Ok/Fail(...)`.
- `ProcessResult` — ExitCode, StandardOutput, StandardError, TimedOut, Elapsed,
  FileName, `.Succeeded` (0/3010/1641), `.RebootRequired`, `.Summary`.
- `WorkingDirectories` — Root/Downloads/Logs/Temp; `WorkingDirectories.Create(root?)`.
- `DeploymentSummary` — Installed/Skipped/Failed/Optimizations lists, TotalElapsed,
  LogDirectory, RebootScheduled; `.Record(result, isOptimization=false)`.
- `AppConfig` (+ `FeatureFlags`, `DownloadSettings`, `SoftwareItem`, `SoftwareCatalog`,
  `MemReductSettings`); `AppConfig.Load(path, out error)`, `.Default()`, `.ShouldReboot`,
  static `AppConfig.JsonOptions`.

## Existing interfaces (namespace `Setup.Core.Abstractions`)
```csharp
interface ILogger {
    event Action<LogEntry>? EntryLogged;
    void Log(LogLevel level, string message, LogCategory category = LogCategory.Install);
    void Info(string message, LogCategory category = LogCategory.Install);
    void Success(string message, LogCategory category = LogCategory.Install);
    void Warning(string message, LogCategory category = LogCategory.Install);
    void Error(string message, Exception? exception = null, LogCategory category = LogCategory.Errors);
    void Download(string message);
    void Flush();
}
interface IProgressReporter {
    void SetPhase(DeploymentPhase phase);
    void SetCurrentTask(string task);
    void SetOverallProgress(double percent);      // 0..100
    void SetCurrentFile(string? fileName);
    void ReportDownload(DownloadProgress progress);
    void TrackItem(string key, string displayName, OperationStatus status);
}
interface IDownloadManager {
    Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
}
interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string arguments = "",
        int timeoutSeconds = 600, string? workingDirectory = null, CancellationToken ct = default);
    Task<ProcessResult> RunPowerShellAsync(string script, int timeoutSeconds = 600, CancellationToken ct = default);
}
interface IInstaller {
    string Name { get; }
    string Key { get; }
    bool IsEnabled(AppConfig config);
    Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken ct = default);
    Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken ct = default);
}
interface IOptimizationTask {
    string Name { get; }
    LogCategory Category => LogCategory.Optimization;
    Task<OperationResult> ApplyAsync(DeploymentContext context, CancellationToken ct = default);
}
```

## Existing context (namespace `Setup.Core.Deployment`)
```csharp
sealed class DeploymentContext {
    AppConfig Config; ILogger Logger; IDownloadManager Downloader;
    IProcessRunner Process; IProgressReporter Reporter; WorkingDirectories Directories;
    DownloadRequest BuildDownload(SoftwareItem item);   // helper
}
```

## Existing utilities (namespace `Setup.Core.Utils`)
- `AdminHelper.IsAdministrator()`.
- `RegistryHelper` — `KeyExists`, `GetValue/GetString/GetDword`, `SetValue/SetDword/SetString`,
  `DeleteValue` (all exception-safe, default `RegistryView.Registry64`). **Use this**
  for all registry work; do not re-implement.

## Integration points owned by the integrator (do NOT create these)
- `App.xaml` / `App.xaml.cs` (startup wiring).
- `Core/Deployment/DeploymentEngine.cs` and `IDeploymentEngine`.
These are written last by the integrator. Your code must be callable from them
purely through the interfaces above.
