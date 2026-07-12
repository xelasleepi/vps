using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Optimization;

/// <summary>
/// Disables Windows "consumer experience" features: cloud-delivered content,
/// suggested/silently-installed apps, Windows tips, lock-screen spotlight,
/// Start-menu suggestions and pre-installed OEM promotions. Purely noise on a
/// dedicated box, and each is a background network/CPU cost.
/// </summary>
/// <remarks>HKCU values target the current (elevated) single user's hive.</remarks>
public sealed class DisableConsumerExperienceTask : OptimizationBase
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string CloudContent = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";

    /// <inheritdoc/>
    public override string Name => "Disable Consumer Experience & Suggestions";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            // Machine-wide policy: no cloud-delivered consumer content.
            RegTweak.Dword(RegistryHive.LocalMachine, CloudContent, "DisableWindowsConsumerFeatures", 1),
            RegTweak.Dword(RegistryHive.LocalMachine, CloudContent, "DisableConsumerAccountStateContent", 1),

            // Per-user Content Delivery Manager suggestion surfaces.
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338389Enabled", 0), // Windows tips
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338388Enabled", 0), // suggested apps
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338387Enabled", 0), // lock screen
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-310093Enabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-353694Enabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SubscribedContent-353696Enabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SystemPaneSuggestionsEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SoftLandingEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "RotatingLockScreenEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "RotatingLockScreenOverlayEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "SilentInstalledAppsEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "ContentDeliveryAllowed", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "PreInstalledAppsEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Cdm, "OemPreInstalledAppsEnabled", 0),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        var result = applied >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} tweaks applied");

        return Task.FromResult(result);
    }
}

/// <summary>
/// Prevents Store/UWP apps from running in the background, both via the per-user
/// master switch and the machine-wide AppPrivacy policy (2 = "Force Deny").
/// </summary>
public sealed class DisableBackgroundAppsTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Disable Background Apps";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            RegTweak.Dword(RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),

            // AppPrivacy policy: 2 = force deny apps running in background.
            RegTweak.Dword(RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        var result = applied >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} tweaks applied");

        return Task.FromResult(result);
    }
}

/// <summary>
/// Disables automatic Microsoft Store app updates (AutoDownload = 2, "never").
/// The deployment installs a curated software set; unsolicited Store churn is
/// unwanted.
/// </summary>
public sealed class DisableAutomaticAppUpdatesTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Disable Automatic App Updates";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            // WindowsStore policy: 2 = disable automatic downloads/updates.
            RegTweak.Dword(RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        var result = applied == total
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} tweaks applied");

        return Task.FromResult(result);
    }
}
