using System.Diagnostics;
using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Installers;

/// <summary>
/// Installs the full set of Microsoft Visual C++ Redistributables listed in
/// <c>config.Software.VisualCppRedistributables</c> (2005 → 2015-2022, x86/x64).
///
/// Each sub-item is detected best-effort; when detection is uncertain the
/// redistributable is installed anyway (the Microsoft silent installers are
/// idempotent and return quickly when already present). The whole set is
/// reported as a single tracked item and one aggregate <see cref="OperationResult"/>.
/// </summary>
public sealed class VisualCppInstaller : InstallerBase
{
    /// <inheritdoc/>
    public override string Name => "Microsoft Visual C++ Redistributables";

    /// <inheritdoc/>
    public override string Key => "vcredist";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallVisualCpp;

    // Not used directly — this installer iterates a list — but required by the base contract.
    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config)
        => config.Software.VisualCppRedistributables.FirstOrDefault() ?? new SoftwareItem { Name = Name };

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var items = context.Config.Software.VisualCppRedistributables;
        if (items.Count == 0)
            return Task.FromResult(true); // nothing to do ⇒ treat as satisfied.

        // Only "already installed" when EVERY configured redistributable is detected.
        var allPresent = items.All(i => IsSubItemInstalled(i));
        return Task.FromResult(allPresent);
    }

    /// <summary>
    /// Installs each configured redistributable, aggregating the outcome into a
    /// single result. If all sub-items are already present the whole step is skipped.
    /// </summary>
    public override async Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        context.Reporter.TrackItem(Key, Name, OperationStatus.InProgress);
        context.Reporter.SetCurrentTask($"Installing {Name}…");

        try
        {
            var items = context.Config.Software.VisualCppRedistributables;
            if (items.Count == 0)
                return Skip(context, sw, "No Visual C++ redistributables configured");

            if (items.All(IsSubItemInstalled))
                return Skip(context, sw, "All Visual C++ redistributables already installed");

            int installed = 0, skipped = 0, failed = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Reporter.SetCurrentTask($"Installing {item.Name}…");

                if (IsSubItemInstalled(item))
                {
                    skipped++;
                    context.Logger.Info($"{item.Name} already present — skipping.", LogCategory.Software);
                    continue;
                }

                try
                {
                    var download = await DownloadItemAsync(context, item, cancellationToken).ConfigureAwait(false);
                    if (!download.Success)
                    {
                        failed++;
                        context.Logger.Warning($"[WARN] {item.Name} download failed: {download.ErrorMessage}", LogCategory.Software);
                        continue;
                    }

                    // Redistributables install fast; a shorter timeout keeps the run moving.
                    var run = await RunInstallerAsync(context, download.FilePath, item.SilentArgs, cancellationToken, timeoutSeconds: 300).ConfigureAwait(false);

                    // Exit 1638 = "another version already installed" ⇒ treat as satisfied.
                    if (run.Succeeded || run.ExitCode == 1638)
                    {
                        installed++;
                        context.Logger.Success($"{item.Name} installed (exit {run.ExitCode}).", LogCategory.Software);
                    }
                    else
                    {
                        failed++;
                        context.Logger.Warning($"[WARN] {item.Name} failed: {run.Summary}", LogCategory.Software);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    context.Logger.Error($"[FAIL] {item.Name} error: {ex.Message}", ex, LogCategory.Errors);
                }
            }

            var summary = $"{installed} installed, {skipped} skipped, {failed} failed";

            if (failed > 0 && installed == 0)
                return Fail(context, sw, $"Visual C++ redistributables: {summary}");

            // Any success (or all pre-existing) counts as an overall success.
            sw.Stop();
            context.Reporter.TrackItem(Key, Name, OperationStatus.Success);
            context.Logger.Success($"[SUCCESS] {Name}: {summary}.", LogCategory.Software);
            return OperationResult.Success(Name, sw.Elapsed, summary);
        }
        catch (OperationCanceledException)
        {
            return Fail(context, sw, $"{Name} cancelled");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Best-effort detection for one redistributable. Modern 2015-2022 packages
    /// expose an <c>Installed</c> flag under the VS 14.0 VC runtime key; older
    /// years are detected via their uninstall DisplayName (year + architecture).
    /// Returns false when uncertain so the (idempotent) installer runs anyway.
    /// </summary>
    private static bool IsSubItemInstalled(SoftwareItem item)
    {
        var name = item.Name;
        var isX64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);
        var isX86 = name.Contains("x86", StringComparison.OrdinalIgnoreCase);

        // 2015-2022 (a.k.a. VC 14.x) — authoritative registry flag.
        if (name.Contains("2015", StringComparison.OrdinalIgnoreCase)
            || name.Contains("2017", StringComparison.OrdinalIgnoreCase)
            || name.Contains("2019", StringComparison.OrdinalIgnoreCase)
            || name.Contains("2022", StringComparison.OrdinalIgnoreCase))
        {
            var arch = isX86 ? "x86" : "x64";
            var subKey = $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}";
            var installedFlag = RegistryHelperGetDword(RegistryHive.LocalMachine, subKey, "Installed");
            var version = RegistryHelperGetString(RegistryHive.LocalMachine, subKey, "Version");
            return installedFlag == 1 && !string.IsNullOrWhiteSpace(version);
        }

        // Older years (2005-2013): match uninstall DisplayName on year + arch.
        var year = ExtractYear(name);
        if (year is null)
            return false;

        var archWord = isX64 ? "x64" : isX86 ? "x86" : null;
        return UninstallDisplayNameContainsAll("Visual C++", year, archWord);
    }

    private static string? ExtractYear(string name)
    {
        foreach (var y in new[] { "2005", "2008", "2010", "2012", "2013" })
            if (name.Contains(y, StringComparison.OrdinalIgnoreCase))
                return y;
        return null;
    }

    /// <summary>Scans uninstall keys for a DisplayName containing every supplied token.</summary>
    private static bool UninstallDisplayNameContainsAll(params string?[] tokens)
    {
        var needed = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToArray();
        if (needed.Length == 0)
            return false;

        const string native = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        return ScanAll(RegistryHive.LocalMachine, native, RegistryView.Registry64, needed)
            || ScanAll(RegistryHive.LocalMachine, wow, RegistryView.Registry32, needed);
    }

    private static bool ScanAll(RegistryHive hive, string subKey, RegistryView view, string[] tokens)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(subKey);
            if (uninstall is null)
                return false;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || display.Length == 0)
                        continue;

                    if (tokens.All(t => display.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { /* keep scanning */ }
            }
        }
        catch { /* hive unavailable */ }

        return false;
    }

    // Thin, exception-safe registry reads (kept local to avoid a hard dependency shape).
    private static int? RegistryHelperGetDword(RegistryHive hive, string subKey, string name)
        => Utils.RegistryHelper.GetDword(hive, subKey, name);

    private static string? RegistryHelperGetString(RegistryHive hive, string subKey, string name)
        => Utils.RegistryHelper.GetString(hive, subKey, name);
}
