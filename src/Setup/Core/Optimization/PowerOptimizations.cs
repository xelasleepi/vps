using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Optimization;

/// <summary>
/// Selects the highest-performance power plan available. Tries to duplicate and
/// activate the hidden <c>Ultimate Performance</c> scheme; if that is not
/// available (some SKUs hide it) it falls back to the built-in
/// <c>High Performance</c> scheme. Logs which plan won.
/// </summary>
public sealed class HighPerformancePowerPlanTask : OptimizationBase
{
    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <inheritdoc/>
    public override string Name => "Set High-Performance Power Plan";

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        // 1) Try Ultimate Performance. Duplicating is idempotent enough — if it
        //    already exists powercfg simply returns the existing GUID.
        await RunToolAsync(context, "powercfg", $"-duplicatescheme {UltimateGuid}", ct).ConfigureAwait(false);
        var setUltimate = await RunToolAsync(context, "powercfg", $"-setactive {UltimateGuid}", ct).ConfigureAwait(false);

        if (setUltimate.Succeeded)
        {
            context.Logger.Info("Activated 'Ultimate Performance' power plan", LogCategory.Optimization);
            return OperationResult.Success(Name, sw.Elapsed, "Ultimate Performance active");
        }

        // 2) Fall back to High Performance.
        context.Logger.Warning(
            "[WARN] Ultimate Performance unavailable — falling back to High Performance",
            LogCategory.Optimization);

        var setHigh = await RunToolAsync(context, "powercfg", $"-setactive {HighPerfGuid}", ct).ConfigureAwait(false);
        if (setHigh.Succeeded)
        {
            context.Logger.Info("Activated 'High Performance' power plan", LogCategory.Optimization);
            return OperationResult.Success(Name, sw.Elapsed, "High Performance active");
        }

        return OperationResult.Failed(Name, sw.Elapsed,
            $"could not activate a performance plan (ultimate exit {setUltimate.ExitCode}, high exit {setHigh.ExitCode})");
    }
}

/// <summary>
/// Disables all idle-timeout power savings: standby, hibernate and monitor
/// timeouts on both AC and DC are set to 0 (never), and hibernation is turned off
/// entirely (also reclaiming <c>hiberfil.sys</c>). Fast Startup is disabled so the
/// machine performs a full, deterministic boot.
/// </summary>
public sealed class DisableSleepAndHibernateTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Disable Sleep, Hibernation & Fast Startup";

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        int ok = 0;
        int total = 0;

        // Never sleep / hibernate / turn off the display (AC and DC).
        string[] timeoutArgs =
        {
            "-change -standby-timeout-ac 0",
            "-change -standby-timeout-dc 0",
            "-change -hibernate-timeout-ac 0",
            "-change -hibernate-timeout-dc 0",
            "-change -monitor-timeout-ac 0",
            "-change -monitor-timeout-dc 0",
        };

        foreach (var arg in timeoutArgs)
        {
            ct.ThrowIfCancellationRequested();
            total++;
            var r = await RunToolAsync(context, "powercfg", arg, ct).ConfigureAwait(false);
            if (r.Succeeded) ok++;
            else context.Logger.Warning($"[WARN] powercfg {arg} failed (exit {r.ExitCode})", LogCategory.Optimization);
        }

        // Disable hibernation entirely (frees hiberfil.sys).
        total++;
        var hib = await RunToolAsync(context, "powercfg", "-hibernate off", ct).ConfigureAwait(false);
        if (hib.Succeeded) ok++;
        else context.Logger.Warning($"[WARN] 'powercfg -hibernate off' failed (exit {hib.ExitCode})", LogCategory.Optimization);

        // Disable Fast Startup (HiberbootEnabled = 0).
        total++;
        if (RegistryHelper.SetDword(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0))
        {
            ok++;
        }
        else
        {
            context.Logger.Warning("[WARN] failed to disable Fast Startup (HiberbootEnabled)", LogCategory.Optimization);
        }

        return ok >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{ok}/{total} settings applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {ok}/{total} settings applied");
    }
}

/// <summary>
/// Disables two latency-inducing power-management features on the active power
/// scheme and re-activates it so the change takes effect:
/// <list type="bullet">
/// <item>USB selective suspend (keeps USB devices always powered).</item>
/// <item>PCI Express Active State Power Management / ASPM (keeps the link at L0).</item>
/// </list>
/// If the current hardware/scheme does not expose a setting, powercfg reports
/// "not supported" and that sub-step is skipped rather than failed.
/// </summary>
public sealed class DisableLinkPowerManagementTask : OptimizationBase
{
    // powercfg subgroup / setting GUIDs.
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string PcieSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <inheritdoc/>
    public override string Name => "Disable USB Selective Suspend & PCIe ASPM";

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        int applied = 0;
        int skipped = 0;
        int failed = 0;

        // USB selective suspend (AC + DC → 0).
        await SetIndexAsync(context, UsbSubgroup, UsbSelectiveSuspend, "USB selective suspend", ct,
            r => Tally(r, ref applied, ref skipped, ref failed)).ConfigureAwait(false);

        // PCIe ASPM (AC + DC → 0 = Off).
        await SetIndexAsync(context, PcieSubgroup, PcieAspm, "PCIe ASPM", ct,
            r => Tally(r, ref applied, ref skipped, ref failed)).ConfigureAwait(false);

        // Commit the changes to the active scheme.
        await RunToolAsync(context, "powercfg", "-setactive SCHEME_CURRENT", ct).ConfigureAwait(false);

        if (applied == 0 && skipped > 0 && failed == 0)
            return OperationResult.Skipped(Name, sw.Elapsed, "settings not supported on this hardware");

        return failed == 0
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied} set, {skipped} unsupported")
            : OperationResult.Failed(Name, sw.Elapsed, $"{applied} set, {skipped} unsupported, {failed} failed");
    }

    /// <summary>Applies AC + DC value-index 0 for a single setting, reporting each outcome.</summary>
    private async Task SetIndexAsync(
        DeploymentContext context, string subgroup, string setting, string label,
        CancellationToken ct, Action<ProcessResult> report)
    {
        var ac = await RunToolAsync(context, "powercfg",
            $"-setacvalueindex SCHEME_CURRENT {subgroup} {setting} 0", ct).ConfigureAwait(false);
        var dc = await RunToolAsync(context, "powercfg",
            $"-setdcvalueindex SCHEME_CURRENT {subgroup} {setting} 0", ct).ConfigureAwait(false);

        // Treat the AC result as representative; DC usually mirrors it.
        if (LooksUnsupported(ac) || LooksUnsupported(dc))
            context.Logger.Warning($"[WARN] {label} not supported — skipping", LogCategory.Optimization);
        else if (ac.Succeeded && dc.Succeeded)
            context.Logger.Info($"{label} disabled (AC+DC)", LogCategory.Optimization);
        else
            context.Logger.Warning($"[WARN] {label} failed (AC exit {ac.ExitCode}, DC exit {dc.ExitCode})", LogCategory.Optimization);

        // Report AC as the representative outcome.
        report(ac);
    }

    private static void Tally(ProcessResult r, ref int applied, ref int skipped, ref int failed)
    {
        if (LooksUnsupported(r)) skipped++;
        else if (r.Succeeded) applied++;
        else failed++;
    }
}
