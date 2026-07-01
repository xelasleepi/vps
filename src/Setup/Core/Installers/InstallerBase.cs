using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Abstractions;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Shared base for every software installer. Provides the common
/// download → silent install → verify template plus reusable helpers for
/// downloading, running installers, winget fallback, registry/file detection
/// and reporter/log-aware result construction.
///
/// Subclasses stay tiny: they supply <see cref="Name"/>, <see cref="Key"/>,
/// <see cref="IsEnabled"/>, <see cref="IsInstalledAsync"/>, the catalog item via
/// <see cref="GetItem"/> and an optional <see cref="VerifyAsync"/>. The template
/// <see cref="InstallAsync"/> orchestrates the rest and never throws.
/// </summary>
public abstract class InstallerBase : IInstaller
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Key { get; }

    /// <inheritdoc/>
    public abstract bool IsEnabled(AppConfig config);

    /// <inheritdoc/>
    public abstract Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>Returns the catalog item this installer consumes from config.</summary>
    protected abstract SoftwareItem GetItem(AppConfig config);

    /// <summary>
    /// Post-install verification. Default implementation re-runs
    /// <see cref="IsInstalledAsync"/>. Override for artifact-based checks.
    /// </summary>
    protected virtual Task<bool> VerifyAsync(DeploymentContext context, string installerPath, CancellationToken ct)
        => IsInstalledAsync(context, ct);

    /// <summary>
    /// Standard install template: track → skip-if-present → download →
    /// silent install (with optional winget fallback) → verify → report.
    /// Wraps everything; converts any exception to a failed result.
    /// </summary>
    public virtual async Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        context.Reporter.TrackItem(Key, Name, OperationStatus.InProgress);
        context.Reporter.SetCurrentTask($"Installing {Name}…");

        try
        {
            if (await IsInstalledAsync(context, cancellationToken).ConfigureAwait(false))
                return Skip(context, sw, $"{Name} already installed");

            var item = GetItem(context.Config);

            // 1) Direct download.
            var download = await DownloadItemAsync(context, item, cancellationToken).ConfigureAwait(false);

            if (download.Success)
            {
                // 2) Silent install of the downloaded payload.
                var run = await RunInstallerAsync(context, download.FilePath, item.SilentArgs, cancellationToken).ConfigureAwait(false);

                if (run.Succeeded && await VerifyAsync(context, download.FilePath, cancellationToken).ConfigureAwait(false))
                    return Ok(context, sw, run.RebootRequired ? "installed (reboot pending)" : "installed");

                context.Logger.Warning($"[WARN] {Name} installer exited {run.ExitCode}; {run.Summary}", LogCategory.Software);
            }
            else
            {
                context.Logger.Warning($"[WARN] {Name} download failed: {download.ErrorMessage}", LogCategory.Software);
            }

            // 3) Winget fallback when the direct path failed.
            if (item.HasWinget && WingetAvailable() && await TryWingetAsync(context, item.WingetId, cancellationToken).ConfigureAwait(false))
            {
                if (await VerifyAsync(context, download.FilePath, cancellationToken).ConfigureAwait(false))
                    return Ok(context, sw, "installed via winget");
            }

            return Fail(context, sw, $"{Name} could not be installed");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------------
    //  Reusable install-step helpers
    // ---------------------------------------------------------------------

    /// <summary>Downloads a catalog item, reporting progress to the UI and logs.</summary>
    protected async Task<DownloadResult> DownloadItemAsync(DeploymentContext context, SoftwareItem item, CancellationToken ct)
    {
        context.Logger.Download($"Downloading {Name}…");
        var request = context.BuildDownload(item);
        var progress = new Progress<DownloadProgress>(p => context.Reporter.ReportDownload(p));
        return await context.Downloader.DownloadAsync(request, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a silent installer in the downloads directory.</summary>
    protected async Task<ProcessResult> RunInstallerAsync(DeploymentContext context, string path, string args, CancellationToken ct, int timeoutSeconds = 600)
    {
        context.Logger.Info($"Running {Name} installer: {Path.GetFileName(path)} {args}".TrimEnd(), LogCategory.Software);
        return await context.Process.RunAsync(path, args, timeoutSeconds, context.Directories.Downloads, ct).ConfigureAwait(false);
    }

    /// <summary>Attempts installation through the Windows Package Manager (winget).</summary>
    protected async Task<bool> TryWingetAsync(DeploymentContext context, string wingetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wingetId))
            return false;

        context.Logger.Info($"Attempting winget fallback for {Name} (id: {wingetId})…", LogCategory.Software);
        var args = $"install --id {wingetId} --silent --accept-source-agreements --accept-package-agreements --disable-interactivity -e";
        var result = await context.Process.RunAsync("winget", args, 900, context.Directories.Downloads, ct).ConfigureAwait(false);

        // winget uses 0 for success; -1978335189 (0x8A15002B) means "already installed".
        if (result.Succeeded || result.ExitCode == unchecked((int)0x8A15002B))
        {
            context.Logger.Success($"winget handled {Name} (exit {result.ExitCode}).", LogCategory.Software);
            return true;
        }

        context.Logger.Warning($"[WARN] winget fallback for {Name} failed: {result.Summary}", LogCategory.Software);
        return false;
    }

    /// <summary>True when <c>winget.exe</c> is resolvable on PATH or in the standard WindowsApps alias location.</summary>
    protected static bool WingetAvailable()
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), "winget.exe")))
                        return true;
                }
                catch { /* skip malformed PATH entries */ }
            }

            var localApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(localApps);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    //  Detection helpers (shared by subclasses)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Scans the standard <c>Uninstall</c> registry keys (HKLM 64-bit, HKLM
    /// WOW6432Node, HKCU) for an entry whose <c>DisplayName</c> contains
    /// <paramref name="displayNameSubstring"/> (case-insensitive).
    /// </summary>
    public static bool UninstallKeyContains(string displayNameSubstring)
    {
        if (string.IsNullOrWhiteSpace(displayNameSubstring))
            return false;

        const string native = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        return ScanUninstall(RegistryHive.LocalMachine, native, RegistryView.Registry64, displayNameSubstring)
            || ScanUninstall(RegistryHive.LocalMachine, wow, RegistryView.Registry32, displayNameSubstring)
            || ScanUninstall(RegistryHive.CurrentUser, native, RegistryView.Registry64, displayNameSubstring)
            || ScanUninstall(RegistryHive.CurrentUser, native, RegistryView.Registry32, displayNameSubstring);
    }

    private static bool ScanUninstall(RegistryHive hive, string subKey, RegistryView view, string needle)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(subKey);
            if (uninstall is null)
                return false;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (!string.IsNullOrEmpty(display) &&
                        display.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* unreadable entry — keep scanning */ }
            }
        }
        catch { /* hive/view unavailable */ }

        return false;
    }

    /// <summary>Returns true if the file at <paramref name="pathWithEnvVars"/> (after %VAR% expansion) exists.</summary>
    public static bool FileExistsExpanded(string pathWithEnvVars)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pathWithEnvVars))
                return false;
            var expanded = Environment.ExpandEnvironmentVariables(pathWithEnvVars);
            return File.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Expands %VAR% tokens in a path (never throws).</summary>
    protected static string Expand(string path)
    {
        try { return Environment.ExpandEnvironmentVariables(path ?? ""); }
        catch { return path ?? ""; }
    }

    // ---------------------------------------------------------------------
    //  Result wrappers (update reporter + log, then build OperationResult)
    // ---------------------------------------------------------------------

    /// <summary>Marks the item skipped, logs a yellow WARN and returns a Skipped result.</summary>
    protected OperationResult Skip(DeploymentContext context, Stopwatch sw, string message)
    {
        sw.Stop();
        context.Reporter.TrackItem(Key, Name, OperationStatus.Skipped);
        context.Logger.Warning($"[WARN] {message}", LogCategory.Software);
        return OperationResult.Skipped(Name, sw.Elapsed, message);
    }

    /// <summary>Marks the item successful, logs a green SUCCESS and returns a Success result.</summary>
    protected OperationResult Ok(DeploymentContext context, Stopwatch sw, string? message = null)
    {
        sw.Stop();
        context.Reporter.TrackItem(Key, Name, OperationStatus.Success);
        context.Logger.Success($"[SUCCESS] {Name} installed.", LogCategory.Software);
        return OperationResult.Success(Name, sw.Elapsed, message);
    }

    /// <summary>Marks the item failed, logs a red error and returns a Failed result.</summary>
    protected OperationResult Fail(DeploymentContext context, Stopwatch sw, string message, Exception? error = null)
    {
        sw.Stop();
        context.Reporter.TrackItem(Key, Name, OperationStatus.Failed);
        context.Logger.Error($"[FAIL] {message}", error, LogCategory.Errors);
        return OperationResult.Failed(Name, sw.Elapsed, message, error);
    }
}
