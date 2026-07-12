using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Optimization;

/// <summary>
/// Disables Xbox Game Bar, Game DVR (background capture) and the Game Mode
/// auto-capture overlay via registry policy and per-user preference keys. These
/// features add input latency and background CPU/disk load with no benefit on a
/// headless/dedicated box.
/// </summary>
/// <remarks>
/// The HKCU values apply to the current (elevated) user's hive, which is the
/// intended single user on the target machine.
/// </remarks>
public sealed class DisableGameDvrTask : OptimizationBase
{
    /// <inheritdoc/>
    public override string Name => "Disable Xbox Game Bar & Game DVR";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            // Master Game DVR switch (per-user).
            RegTweak.Dword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0),

            // Machine-wide policy: forbid Game DVR entirely.
            RegTweak.Dword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0),

            // Game Bar preferences.
            RegTweak.Dword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 0),

            // App capture (background recording) off.
            RegTweak.Dword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        var result = applied >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} tweaks applied");

        return Task.FromResult(result);
    }
}
