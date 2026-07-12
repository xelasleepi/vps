using System.Diagnostics;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Optimization;

/// <summary>
/// Frees disk space by emptying a fixed set of well-known scratch directories
/// (per-user Temp, Windows Temp, Prefetch). Locked/in-use files are skipped, and
/// <em>only</em> the enumerated known folders are ever touched — nothing outside
/// them is deleted. Bytes freed are reported in the result message.
/// </summary>
public sealed class CleanTempFoldersTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Clean Temporary Files (Temp, Windows Temp, Prefetch)";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        // Distinct, existing target directories only.
        var targets = new List<string>();
        AddIfPresent(targets, Environment.GetEnvironmentVariable("TEMP"));
        AddIfPresent(targets, Environment.GetEnvironmentVariable("TMP"));
        AddIfPresent(targets, Path.Combine(SystemRoot(), "Temp"));
        AddIfPresent(targets, Path.Combine(SystemRoot(), "Prefetch"));

        if (targets.Count == 0)
            return Task.FromResult(OperationResult.Skipped(Name, sw.Elapsed, "no temp folders found"));

        long totalFreed = 0;
        int totalFiles = 0;

        foreach (var dir in targets)
        {
            ct.ThrowIfCancellationRequested();
            long freed = FileSystemUtil.TryEmptyDirectory(dir, out int files);
            totalFreed += freed;
            totalFiles += files;
            context.Logger.Info(
                $"Cleaned {dir}: {files} files, {FileSystemUtil.HumanBytes(freed)}",
                LogCategory.Optimization);
        }

        return Task.FromResult(OperationResult.Success(Name, sw.Elapsed,
            $"freed {FileSystemUtil.HumanBytes(totalFreed)} across {totalFiles} files"));
    }

    private static void AddIfPresent(List<string> list, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full) &&
                !list.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(full);
            }
        }
        catch
        {
            // Ignore malformed environment paths.
        }
    }

    private static string SystemRoot()
        => Environment.GetEnvironmentVariable("SystemRoot")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}

/// <summary>
/// Reclaims the Windows Update download cache. Stops <c>wuauserv</c> and
/// <c>bits</c>, empties <c>%SystemRoot%\SoftwareDistribution\Download</c>, then
/// restarts the services. If the services cannot be stopped the emptying step is
/// skipped (the folder would be locked) rather than forced, and the task reports
/// a skip. Only the Download sub-folder is ever touched.
/// </summary>
public sealed class CleanWindowsUpdateCacheTask : OptimizationBase
{
    private static readonly string[] Services = { "wuauserv", "bits" };

    /// <inheritdoc/>
    public override string Name => "Clean Windows Update Cache";

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var downloadDir = Path.Combine(SystemRoot(), "SoftwareDistribution", "Download");

        if (!Directory.Exists(downloadDir))
            return OperationResult.Skipped(Name, sw.Elapsed, "SoftwareDistribution\\Download not present");

        // 1) Stop the services so the files are unlocked.
        bool allStopped = true;
        foreach (var svc in Services)
        {
            ct.ThrowIfCancellationRequested();
            var stop = await RunToolAsync(context, "sc", $"stop \"{svc}\"", ct).ConfigureAwait(false);
            bool ok = stop.Succeeded || StopIsNoop(stop);
            if (!ok)
            {
                allStopped = false;
                context.Logger.Warning(
                    $"[WARN] could not stop '{svc}' (exit {stop.ExitCode}) — skipping cache clean to avoid locked files",
                    LogCategory.Optimization);
            }
        }

        long freed = 0;
        int files = 0;
        bool cleaned = false;

        if (allStopped)
        {
            // Give the services a brief moment to release handles.
            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }

            freed = FileSystemUtil.TryEmptyDirectory(downloadDir, out files);
            cleaned = true;
            context.Logger.Info(
                $"Emptied update cache: {files} files, {FileSystemUtil.HumanBytes(freed)}",
                LogCategory.Optimization);
        }

        // 2) Always attempt to restart the services, even if we skipped cleaning.
        foreach (var svc in Services)
        {
            var start = await RunToolAsync(context, "sc", $"start \"{svc}\"", ct).ConfigureAwait(false);
            if (!start.Succeeded && !StartIsNoop(start))
                context.Logger.Warning($"[WARN] could not restart '{svc}' (exit {start.ExitCode})", LogCategory.Optimization);
        }

        if (!cleaned)
            return OperationResult.Skipped(Name, sw.Elapsed, "update services could not be stopped");

        return OperationResult.Success(Name, sw.Elapsed,
            $"freed {FileSystemUtil.HumanBytes(freed)} across {files} files");
    }

    /// <summary>A stop request is a no-op when the service is absent or already stopped.</summary>
    private static bool StopIsNoop(ProcessResult r)
    {
        var text = (r.StandardOutput + " " + r.StandardError).ToLowerInvariant();
        return r.ExitCode is 1060 or 1062        // does not exist / not started
            || text.Contains("not been started")
            || text.Contains("does not exist")
            || text.Contains("not started");
    }

    /// <summary>A start request is a no-op when the service is already running.</summary>
    private static bool StartIsNoop(ProcessResult r)
    {
        var text = (r.StandardOutput + " " + r.StandardError).ToLowerInvariant();
        return r.ExitCode == 1056                 // already running
            || text.Contains("already running")
            || text.Contains("already been started");
    }

    private static string SystemRoot()
        => Environment.GetEnvironmentVariable("SystemRoot")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}
