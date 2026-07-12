using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Installs Mem Reduct silently (Inno Setup:
/// <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS</c>) and then applies
/// the desired behaviour from <c>config.MemReductSettings</c>:
/// <list type="bullet">
///   <item>Autostart via the HKCU <c>Run</c> key.</item>
///   <item>A best-effort <c>memreduct.ini</c> enabling start-minimized, tray,
///   and auto-clean with the configured threshold/interval.</item>
/// </list>
/// Configuration is applied best-effort — the install still succeeds (with a
/// WARN) if a config step fails.
/// </summary>
public sealed class MemReductInstaller : InstallerBase
{
    private const string ExpectedExeRelative = @"Mem Reduct\memreduct.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mem Reduct";

    /// <inheritdoc/>
    public override string Name => "Mem Reduct";

    /// <inheritdoc/>
    public override string Key => "memreduct";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallMemReduct;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.MemReduct;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ResolveExePath() is not null || UninstallKeyContains("Mem Reduct"));

    /// <summary>Install, then configure autostart + INI settings.</summary>
    public override async Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        context.Reporter.TrackItem(Key, Name, OperationStatus.InProgress);
        context.Reporter.SetCurrentTask($"Installing {Name}…");

        try
        {
            if (await IsInstalledAsync(context, cancellationToken).ConfigureAwait(false))
            {
                // Already installed — still (re)apply configuration, then skip.
                var existing = ResolveExePath();
                if (existing is not null)
                    Configure(context, existing);
                return Skip(context, sw, $"{Name} already installed");
            }

            var item = GetItem(context.Config);

            var download = await DownloadItemAsync(context, item, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
                return Fail(context, sw, $"{Name} download failed: {download.ErrorMessage}");

            var run = await RunInstallerAsync(context, download.FilePath, item.SilentArgs, cancellationToken).ConfigureAwait(false);
            if (!run.Succeeded)
                return Fail(context, sw, $"{Name} installer failed: {run.Summary}");

            var exePath = ResolveExePath();
            if (exePath is null)
                return Fail(context, sw, "memreduct.exe not found after install");

            // Configure autostart + settings (best-effort, never fatal).
            Configure(context, exePath);

            return Ok(context, sw, run.RebootRequired ? "installed (reboot pending)" : "installed & configured");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
    }

    /// <summary>Applies autostart + INI configuration per <c>MemReductSettings</c>.</summary>
    private static void Configure(DeploymentContext context, string exePath)
    {
        var settings = context.Config.MemReductSettings;

        // --- Autostart: HKCU\...\Run value pointing at the exe. -------------
        try
        {
            if (settings.Autostart)
            {
                var ok = RegistryHelper.SetString(RegistryHive.CurrentUser, RunKey, RunValueName, $"\"{exePath}\"");
                if (ok)
                    context.Logger.Info($"Mem Reduct autostart registered ({RunValueName} → \"{exePath}\").", LogCategory.Software);
                else
                    context.Logger.Warning("[WARN] Failed to register Mem Reduct autostart Run value.", LogCategory.Software);
            }
            else
            {
                RegistryHelper.DeleteValue(RegistryHive.CurrentUser, RunKey, RunValueName);
            }
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[WARN] Mem Reduct autostart config failed: {ex.Message}", LogCategory.Software);
        }

        // --- INI settings (portable next to exe + roaming profile). --------
        try
        {
            var ini = BuildIni(settings);

            var portableIni = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "memreduct.ini");
            WriteIniSafe(context, portableIni, ini);

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roamingIni = Path.Combine(appData, "Henry++", "Mem Reduct", "memreduct.ini");
            WriteIniSafe(context, roamingIni, ini);

            context.Logger.Info(
                $"Mem Reduct configured: Autorun={settings.Autostart}, MinimizeToTray={settings.MinimizeToTray}, " +
                $"AutoClean={settings.AutoCleanEnabled} @ {settings.CleanThresholdPercent}% / {settings.CleanIntervalMinutes}min.",
                LogCategory.Software);
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[WARN] Mem Reduct INI config failed: {ex.Message}", LogCategory.Software);
        }
    }

    /// <summary>
    /// Builds a best-effort <c>[settings]</c> INI body. Exact key names vary by
    /// Mem Reduct version, so we write the commonly-recognised keys and log what
    /// was applied; unknown keys are ignored harmlessly by the app.
    /// </summary>
    private static string BuildIni(MemReductSettings s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[settings]");
        sb.AppendLine($"Autorun={Bool(s.Autostart)}");
        sb.AppendLine($"CheckUpdates=0");
        sb.AppendLine($"TrayUseTransparency={Bool(s.MinimizeToTray)}");
        sb.AppendLine($"MinimizeToTray={Bool(s.MinimizeToTray)}");
        sb.AppendLine($"HideOnClose={Bool(s.MinimizeToTray)}");
        sb.AppendLine($"StartMinimized={Bool(s.StartMinimized)}");
        // Auto-clean of physical memory: master toggle, threshold %, interval (minutes).
        sb.AppendLine($"AutoclearEnable={Bool(s.AutoCleanEnabled)}");
        sb.AppendLine($"AutoclearReductEnable={Bool(s.AutoCleanEnabled)}");
        sb.AppendLine($"AutoclearReductValue={s.CleanThresholdPercent}");
        sb.AppendLine($"AutoclearIntervalEnable={Bool(s.AutoCleanEnabled)}");
        sb.AppendLine($"AutoclearIntervalValue={s.CleanIntervalMinutes}");
        return sb.ToString();

        static string Bool(bool b) => b ? "true" : "false";
    }

    /// <summary>Writes/overwrites an INI file, creating parent folders. Best-effort.</summary>
    private static void WriteIniSafe(DeploymentContext context, string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[WARN] Could not write Mem Reduct INI '{path}': {ex.Message}", LogCategory.Software);
        }
    }

    /// <summary>Locates the installed <c>memreduct.exe</c> in the common install roots.</summary>
    private static string? ResolveExePath()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, ExpectedExeRelative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
