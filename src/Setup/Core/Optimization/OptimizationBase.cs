using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Abstractions;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Optimization;

/// <summary>
/// Shared plumbing for <see cref="IOptimizationTask"/> implementations in this
/// module. Provides consistent start/finish logging to
/// <see cref="LogCategory.Optimization"/>, a resilient <see cref="Stopwatch"/>
/// wrapper that guarantees no exception ever crosses the task boundary, and small
/// helpers for the two things every task does: apply a batch of registry tweaks
/// and shell out to a Windows CLI tool (<c>powercfg</c>, <c>sc</c>,
/// <c>schtasks</c>).
/// </summary>
/// <remarks>
/// HKCU tweaks apply to the current (elevated) user's hive. On a dedicated,
/// single-user Tiny10 box that is exactly the intended target, so no per-user
/// hive loading is attempted.
/// </remarks>
public abstract class OptimizationBase : IOptimizationTask
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public LogCategory Category => LogCategory.Optimization;

    /// <summary>
    /// Runs the concrete work under a stopwatch, logs a start line, and converts
    /// any escaped exception into a <see cref="OperationResult.Failed"/> so the
    /// optimizer keeps going. Concrete tasks implement <see cref="RunAsync"/>.
    /// </summary>
    public async Task<OperationResult> ApplyAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        context.Reporter.SetCurrentTask(Name);
        context.Logger.Info($"Starting: {Name}", LogCategory.Optimization);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RunAsync(context, sw, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            LogResult(context, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var result = OperationResult.Skipped(Name, sw.Elapsed, "cancelled");
            LogResult(context, result);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            context.Logger.Error($"[ERROR] {Name} threw: {ex.Message}", ex, LogCategory.Optimization);
            return OperationResult.Failed(Name, sw.Elapsed, ex.Message, ex);
        }
    }

    /// <summary>
    /// Performs the optimization. Implementations must be idempotent and should
    /// return via the <see cref="OperationResult"/> factories; any exception that
    /// escapes is caught by <see cref="ApplyAsync"/> and reported as a failure.
    /// </summary>
    protected abstract Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct);

    /// <summary>Writes the standard [SUCCESS]/[WARN]/[ERROR] finish line for a result.</summary>
    private void LogResult(DeploymentContext context, OperationResult result)
    {
        var line = result.StatusLine();
        switch (result.Status)
        {
            case OperationStatus.Success:
                context.Logger.Success($"[SUCCESS] {line}", LogCategory.Optimization);
                break;
            case OperationStatus.Skipped:
                context.Logger.Warning($"[WARN] {line}", LogCategory.Optimization);
                break;
            case OperationStatus.Failed:
                context.Logger.Error($"[ERROR] {line}", null, LogCategory.Optimization);
                break;
            default:
                context.Logger.Info(line, LogCategory.Optimization);
                break;
        }
    }

    // ---- Registry helpers ---------------------------------------------------

    /// <summary>A single registry tweak: hive + sub-key + value name + value + kind.</summary>
    protected readonly record struct RegTweak(
        RegistryHive Hive,
        string SubKey,
        string Name,
        object Value,
        RegistryValueKind Kind = RegistryValueKind.DWord)
    {
        /// <summary>Convenience factory for a DWORD tweak.</summary>
        public static RegTweak Dword(RegistryHive hive, string subKey, string name, int value)
            => new(hive, subKey, name, value, RegistryValueKind.DWord);

        /// <summary>Convenience factory for a string tweak.</summary>
        public static RegTweak Str(RegistryHive hive, string subKey, string name, string value)
            => new(hive, subKey, name, value, RegistryValueKind.String);
    }

    /// <summary>
    /// Applies a batch of registry tweaks, logging any that fail as WARN. Returns
    /// the number of tweaks that were written successfully.
    /// </summary>
    protected static int ApplyTweaks(DeploymentContext context, IEnumerable<RegTweak> tweaks, out int total)
    {
        int applied = 0;
        total = 0;
        foreach (var t in tweaks)
        {
            total++;
            var ok = RegistryHelper.SetValue(t.Hive, t.SubKey, t.Name, t.Value, t.Kind);
            if (ok)
            {
                applied++;
            }
            else
            {
                context.Logger.Warning(
                    $"[WARN] failed to write {HiveShort(t.Hive)}\\{t.SubKey}\\{t.Name}",
                    LogCategory.Optimization);
            }
        }
        return applied;
    }

    private static string HiveShort(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString()
    };

    // ---- Process helpers ----------------------------------------------------

    /// <summary>
    /// Runs a CLI tool and returns its <see cref="ProcessResult"/>. Never throws —
    /// on failure to even launch, a synthetic non-zero result is returned by the
    /// process runner. A short timeout keeps a stuck tool from stalling the run.
    /// </summary>
    protected static Task<ProcessResult> RunToolAsync(
        DeploymentContext context, string fileName, string arguments, CancellationToken ct, int timeoutSeconds = 60)
        => context.Process.RunAsync(fileName, arguments, timeoutSeconds, null, ct);

    /// <summary>
    /// True when a tool's combined output indicates the target simply does not
    /// exist (service/task/scheme absent) — which we treat as "already gone" and
    /// therefore a skip rather than a failure. Tiny10 removes many components.
    /// </summary>
    protected static bool LooksMissing(ProcessResult r)
    {
        var text = (r.StandardOutput + " " + r.StandardError).ToLowerInvariant();
        return text.Contains("does not exist")
            || text.Contains("was not found")
            || text.Contains("cannot find")
            || text.Contains("specified service does not exist")
            || text.Contains("element not found")
            || text.Contains("the system cannot find")
            || text.Contains("no mapping between account names")
            // sc.exe error codes: 1060 (service does not exist), 1168 (element not found)
            || r.ExitCode is 1060 or 1168;
    }

    /// <summary>
    /// True when output indicates a not-supported / not-applicable condition
    /// (e.g. powercfg on a scheme/setting the hardware doesn't expose). Treated as
    /// a skip.
    /// </summary>
    protected static bool LooksUnsupported(ProcessResult r)
    {
        var text = (r.StandardOutput + " " + r.StandardError).ToLowerInvariant();
        return text.Contains("not supported")
            || text.Contains("invalid parameters")
            || text.Contains("unable to perform operation")
            || text.Contains("does not support");
    }
}
