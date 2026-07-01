using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Optimization;

/// <summary>
/// Applies core OS responsiveness tweaks: processor scheduling favours background
/// services, visual effects and transparency are minimised for performance,
/// Explorer startup delay is removed, and the automatic scheduled maintenance /
/// defrag tasks are disabled so they never wake or thrash the box.
/// </summary>
/// <remarks>HKCU values target the current (elevated) single user's hive.</remarks>
public sealed class SystemPerformanceTweaksTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Apply System Performance Tweaks";

    /// <inheritdoc/>
    protected override async Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            // Processor scheduling: 0x18 (24) = optimise for background services.
            RegTweak.Dword(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x18),

            // Visual effects: 2 = "Adjust for best performance".
            RegTweak.Dword(RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2),

            // Transparency off.
            RegTweak.Dword(RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),

            // No artificial startup-app delay.
            RegTweak.Dword(RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),

            // Disable automatic scheduled maintenance.
            RegTweak.Dword(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", 1),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        // Disable the scheduled defrag task. Missing task → skipped, not failed.
        var defrag = await RunToolAsync(context, "schtasks",
            "/Change /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Disable", ct).ConfigureAwait(false);

        if (defrag.Succeeded)
            context.Logger.Info("Scheduled defrag task disabled", LogCategory.Optimization);
        else if (LooksMissing(defrag))
            context.Logger.Warning("[WARN] ScheduledDefrag task not present — skipping", LogCategory.Optimization);
        else
            context.Logger.Warning($"[WARN] could not disable ScheduledDefrag (exit {defrag.ExitCode})", LogCategory.Optimization);

        // Result driven by the registry tweaks (the defrag task is best-effort).
        return applied >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} registry tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} registry tweaks applied");
    }
}
