using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Optimization;

/// <summary>
/// Configures File Explorer for administrator/power-user defaults: show file
/// extensions and hidden files, open to "This PC" instead of Quick Access, and
/// stop tracking recent/frequent documents.
/// </summary>
/// <remarks>HKCU values target the current (elevated) single user's hive.</remarks>
public sealed class ExplorerPreferencesTask : OptimizationBase
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    /// <inheritdoc/>
    public override string Name => "Configure Explorer (extensions, hidden files, This PC, no recent/frequent)";

    /// <inheritdoc/>
    protected override Task<OperationResult> RunAsync(DeploymentContext context, Stopwatch sw, CancellationToken ct)
    {
        var tweaks = new[]
        {
            // Show known file extensions (0 = do not hide).
            RegTweak.Dword(RegistryHive.CurrentUser, Advanced, "HideFileExt", 0),

            // Show hidden files (1 = show).
            RegTweak.Dword(RegistryHive.CurrentUser, Advanced, "Hidden", 1),

            // Launch Explorer to "This PC" (1) rather than Quick Access (2).
            RegTweak.Dword(RegistryHive.CurrentUser, Advanced, "LaunchTo", 1),

            // Stop tracking recently opened documents in the Start menu.
            RegTweak.Dword(RegistryHive.CurrentUser, Advanced, "Start_TrackDocs", 0),

            // Hide recent files and frequent folders in Quick Access.
            RegTweak.Dword(RegistryHive.CurrentUser, Explorer, "ShowRecent", 0),
            RegTweak.Dword(RegistryHive.CurrentUser, Explorer, "ShowFrequent", 0),
        };

        int applied = ApplyTweaks(context, tweaks, out int total);

        var result = applied >= (total / 2) + 1
            ? OperationResult.Success(Name, sw.Elapsed, $"{applied}/{total} tweaks applied")
            : OperationResult.Failed(Name, sw.Elapsed, $"only {applied}/{total} tweaks applied");

        return Task.FromResult(result);
    }
}
