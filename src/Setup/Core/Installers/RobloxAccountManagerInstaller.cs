using System.Diagnostics;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Installs Roblox Account Manager — a portable app shipped as a ZIP. There is
/// no silent installer: the archive is downloaded and extracted to
/// <c>%ProgramFiles%\Roblox Account Manager</c> (falling back to
/// <c>%LOCALAPPDATA%\Roblox Account Manager</c> when Program Files is not
/// writable). A best-effort Start-Menu shortcut is created to the main exe.
/// </summary>
public sealed class RobloxAccountManagerInstaller : InstallerBase
{
    private const string FolderName = "Roblox Account Manager";

    /// <inheritdoc/>
    public override string Name => "Roblox Account Manager";

    /// <inheritdoc/>
    public override string Key => "robloxaccountmanager";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallRobloxAccountManager;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.RobloxAccountManager;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(FindInstalledExe() is not null);

    /// <summary>Download the ZIP, extract to the target folder, locate the exe, add a shortcut.</summary>
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

            // Choose target: Program Files first, fall back to LocalAppData.
            var targetDir = ChooseTargetDir(context);
            context.Logger.Info($"Extracting {Name} to {targetDir}…", LogCategory.Software);

            if (!FileSystemUtil.TryExtractZip(download.FilePath, targetDir, overwrite: true))
                return Fail(context, sw, $"Failed to extract {Name} archive");

            var exePath = FindExeInTree(targetDir);
            if (exePath is null)
                return Fail(context, sw, "Account Manager executable not found after extraction");

            // Best-effort Start-Menu shortcut.
            await TryCreateShortcutAsync(context, exePath, cancellationToken).ConfigureAwait(false);

            return Ok(context, sw, $"extracted to {targetDir}");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
    }

    /// <summary>Picks a writable install directory, preferring Program Files.</summary>
    private static string ChooseTargetDir(DeploymentContext context)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfTarget = Path.Combine(programFiles, FolderName);
        if (IsWritable(pfTarget))
            return pfTarget;

        context.Logger.Warning($"[WARN] Program Files not writable; using LocalAppData for {FolderName}.", LogCategory.Software);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, FolderName);
    }

    /// <summary>Tests whether a directory can be created/written (creates it as a side-effect on success).</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_probe.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates a Start-Menu shortcut to the exe via a PowerShell one-liner (best-effort).</summary>
    private async Task TryCreateShortcutAsync(DeploymentContext context, string exePath, CancellationToken ct)
    {
        try
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            var lnkPath = Path.Combine(startMenu, $"{Name}.lnk");
            var workingDir = Path.GetDirectoryName(exePath) ?? "";

            var script =
                "$w = New-Object -ComObject WScript.Shell; " +
                $"$s = $w.CreateShortcut('{lnkPath}'); " +
                $"$s.TargetPath = '{exePath}'; " +
                $"$s.WorkingDirectory = '{workingDir}'; " +
                "$s.Save()";

            var result = await context.Process.RunPowerShellAsync(script, 60, ct).ConfigureAwait(false);
            if (result.Succeeded)
                context.Logger.Info($"Created Start-Menu shortcut for {Name}.", LogCategory.Software);
            else
                context.Logger.Warning($"[WARN] Could not create {Name} shortcut: {result.Summary}", LogCategory.Software);
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[WARN] Shortcut creation for {Name} failed: {ex.Message}", LogCategory.Software);
        }
    }

    /// <summary>Returns the installed exe path if the target folder already contains one.</summary>
    private static string? FindInstalledExe()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var dir = Path.Combine(root, FolderName);
            var exe = FindExeInTree(dir);
            if (exe is not null)
                return exe;
        }
        return null;
    }

    /// <summary>
    /// Locates the main Account Manager executable in an extracted tree. Prefers
    /// an exe whose name references the account manager; otherwise the first exe.
    /// </summary>
    private static string? FindExeInTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return null;

            var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).ToList();
            if (exes.Count == 0)
                return null;

            var preferred = exes.FirstOrDefault(e =>
            {
                var n = Path.GetFileNameWithoutExtension(e);
                return n.Contains("Account", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("RAM", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Roblox", StringComparison.OrdinalIgnoreCase);
            });

            // Avoid picking an obvious uninstaller/updater if a better match exists.
            return preferred
                ?? exes.FirstOrDefault(e => !Path.GetFileName(e).Contains("unins", StringComparison.OrdinalIgnoreCase))
                ?? exes[0];
        }
        catch
        {
            return null;
        }
    }
}
