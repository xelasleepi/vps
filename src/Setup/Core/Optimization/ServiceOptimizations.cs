using System.Diagnostics;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Optimization;

/// <summary>
/// Disables one or more Windows services by name using <c>sc.exe</c>: sets the
/// start type to <c>disabled</c> and then stops the running service. A service
/// that Tiny10 has already removed reports "service does not exist" — that is a
/// <see cref="OperationStatus.Skipped"/>, not a failure. "Already stopped" is a
/// success.
/// </summary>
/// <remarks>
/// One task can target several related services (e.g. all Xbox services) and is
/// considered successful when the majority of its targets were disabled or were
/// already absent.
/// </remarks>
public sealed class DisableServicesTask : OptimizationBase
{
    private readonly string[] _services;

    /// <inheritdoc/>
    public override string Name { get; }

    /// <summary>
    /// Creates a service-disabling task.
    /// </summary>
    /// <param name="name">Human-readable task name, e.g. "Disable SysMain".</param>
    /// <param name="services">One or more service short names, e.g. <c>SysMain</c>.</param>
    public DisableServicesTask(string name, params string[] services)
    {
        Name = name;
        _services = services;
    }

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        int handled = 0;   // disabled or already-absent
        int missing = 0;
        int failed = 0;

        foreach (var svc in _services)
        {
            ct.ThrowIfCancellationRequested();

            // 1) Set start type to disabled. Note the required space after "start=".
            var config = await RunToolAsync(context, "sc", $"config \"{svc}\" start= disabled", ct).ConfigureAwait(false);

            if (LooksMissing(config))
            {
                context.Logger.Warning($"[WARN] service '{svc}' not present — skipping", LogCategory.Optimization);
                missing++;
                continue;
            }

            if (!config.Succeeded)
            {
                context.Logger.Warning(
                    $"[WARN] '{svc}' config failed (exit {config.ExitCode}): {Trim(config.StandardError)}",
                    LogCategory.Optimization);
                failed++;
                // still attempt to stop it below
            }

            // 2) Stop the service. "not started" / "already stopped" is fine.
            var stop = await RunToolAsync(context, "sc", $"stop \"{svc}\"", ct).ConfigureAwait(false);
            bool stopOk = stop.Succeeded
                || stop.ExitCode == 1062          // service has not been started
                || StopAlreadyStopped(stop);

            if (config.Succeeded)
            {
                handled++;
                context.Logger.Info(
                    stopOk ? $"'{svc}' disabled and stopped" : $"'{svc}' disabled (stop deferred to reboot)",
                    LogCategory.Optimization);
            }
        }

        int total = _services.Length;

        // All targets were absent → nothing to do.
        if (missing == total)
            return OperationResult.Skipped(Name, sw.Elapsed, $"no target services present ({total})");

        // Majority-applied rule: succeed when most targets were handled (disabled
        // or already absent). Individual failures were logged as WARN above.
        int effective = handled + missing;
        return effective > total / 2
            ? OperationResult.Success(Name, sw.Elapsed, Summary(handled, missing, failed, total))
            : OperationResult.Failed(Name, sw.Elapsed, Summary(handled, missing, failed, total));
    }

    private static string Summary(int handled, int missing, int failed, int total)
        => $"{handled} disabled, {missing} absent, {failed} failed of {total}";

    private static bool StopAlreadyStopped(ProcessResult r)
    {
        var text = (r.StandardOutput + " " + r.StandardError).ToLowerInvariant();
        return text.Contains("not been started")
            || text.Contains("not started")
            || text.Contains("already been stopped")
            || LooksMissingCode(r);
    }

    private static bool LooksMissingCode(ProcessResult r) => r.ExitCode is 1060 or 1062;

    private static string Trim(string s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().Replace("\r", " ").Replace("\n", " ");

    // ---- Catalog of concrete service-disable tasks -------------------------

    /// <summary>Disable SysMain (formerly Superfetch) — reduces disk/CPU churn on SSD boxes.</summary>
    public static DisableServicesTask SysMain() =>
        new("Disable SysMain", "SysMain");

    /// <summary>Disable the Windows Search indexer service (WSearch).</summary>
    public static DisableServicesTask WindowsSearch() =>
        new("Disable Windows Search indexing", "WSearch");

    /// <summary>Disable Delivery Optimization (peer-to-peer update sharing).</summary>
    public static DisableServicesTask DeliveryOptimization() =>
        new("Disable Delivery Optimization", "DoSvc");

    /// <summary>Disable all Xbox-related services in one task.</summary>
    public static DisableServicesTask XboxServices() =>
        new("Disable Xbox Services", "XblAuthManager", "XblGameSave", "XboxGipSvc", "XboxNetApiSvc");
}
