using System.Diagnostics;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Installers;

/// <summary>
/// Installs Roblox via the web bootstrapper. The bootstrapper installs the
/// player silently by itself and then tries to launch the game; we run it, poll
/// for <c>RobloxPlayerBeta.exe</c> to appear under the per-user Versions folder,
/// then kill any launched player/launcher process so the run stays unattended.
///
/// NOTE: Roblox installs <em>per-user</em>, into the profile of the (elevated)
/// user running Setup.exe — i.e. under this account's <c>%LOCALAPPDATA%\Roblox</c>.
/// </summary>
public sealed class RobloxInstaller : InstallerBase
{
    private const int PollTimeoutSeconds = 180;
    private const int PollIntervalMs = 3000;

    /// <inheritdoc/>
    public override string Name => "Roblox";

    /// <inheritdoc/>
    public override string Key => "roblox";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallRoblox;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.Roblox;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(FindPlayerExe() is not null);

    /// <summary>Runs the bootstrapper, polls for the player exe, then stops any launched process.</summary>
    public override async Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        context.Reporter.TrackItem(Key, Name, OperationStatus.InProgress);
        context.Reporter.SetCurrentTask($"Installing {Name}…");

        try
        {
            if (await IsInstalledAsync(context, cancellationToken).ConfigureAwait(false))
                return Skip(context, sw, $"{Name} already installed");

            var item = GetItem(context.Config);

            var download = await DownloadItemAsync(context, item, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
                return Fail(context, sw, $"{Name} download failed: {download.ErrorMessage}");

            // The bootstrapper self-installs and then launches; don't wait for it to
            // exit — fire it and poll the filesystem for the installed player.
            context.Logger.Info("Running Roblox bootstrapper…", LogCategory.Software);
            _ = context.Process.RunAsync(download.FilePath, item.SilentArgs, PollTimeoutSeconds + 30, context.Directories.Downloads, cancellationToken);

            context.Reporter.SetCurrentTask("Waiting for Roblox to finish installing…");
            var appeared = await PollForPlayerAsync(cancellationToken).ConfigureAwait(false);

            // Whether or not it appeared, stop any launched player/launcher so the run stays silent.
            KillRobloxProcesses(context);

            if (!appeared)
                return Fail(context, sw, $"RobloxPlayerBeta.exe did not appear within {PollTimeoutSeconds}s");

            return Ok(context, sw, "installed");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
    }

    /// <summary>Polls up to <see cref="PollTimeoutSeconds"/> for the player exe to be written.</summary>
    private static async Task<bool> PollForPlayerAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(PollTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (FindPlayerExe() is not null)
                return true;
            try
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return FindPlayerExe() is not null;
            }
        }
        return FindPlayerExe() is not null;
    }

    /// <summary>
    /// Finds any <c>RobloxPlayerBeta.exe</c> under
    /// <c>%LOCALAPPDATA%\Roblox\Versions\</c>, or null when none exists.
    /// </summary>
    private static string? FindPlayerExe()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var versions = Path.Combine(local, "Roblox", "Versions");
            if (!Directory.Exists(versions))
                return null;

            return Directory.EnumerateFiles(versions, "RobloxPlayerBeta.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort termination of any launched Roblox player/launcher processes.</summary>
    private static void KillRobloxProcesses(DeploymentContext context)
    {
        foreach (var name in new[] { "RobloxPlayerBeta", "RobloxPlayerLauncher", "RobloxPlayer" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        context.Logger.Info($"Stopped launched process '{name}'.", LogCategory.Software);
                    }
                    catch { /* already exiting / access denied */ }
                    finally { proc.Dispose(); }
                }
            }
            catch { /* enumeration failure — ignore */ }
        }
    }
}
