using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Installs the Microsoft Edge WebView2 Evergreen Runtime silently
/// (<c>/silent /install</c>). Detection reads the EdgeUpdate client
/// <c>pv</c> (product version) for the WebView2 runtime GUID across the
/// machine (64-bit and WOW6432Node) and per-user hives; a non-empty version
/// other than <c>0.0.0.0</c> indicates the runtime is present.
/// </summary>
public sealed class WebView2Installer : InstallerBase
{
    /// <summary>Stable GUID of the WebView2 Evergreen Runtime under EdgeUpdate\Clients.</summary>
    private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <inheritdoc/>
    public override string Name => "Microsoft Edge WebView2 Runtime";

    /// <inheritdoc/>
    public override string Key => "webview2";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallWebView2;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.WebView2;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(DetectRuntimeVersion() is not null);

    /// <inheritdoc/>
    protected override Task<bool> VerifyAsync(DeploymentContext context, string installerPath, CancellationToken ct)
        => IsInstalledAsync(context, ct);

    /// <summary>Returns the installed runtime version, or null when not present.</summary>
    private static string? DetectRuntimeVersion()
    {
        // WOW6432Node under HKLM (typical on 64-bit Windows), plain HKLM, then HKCU.
        var locations = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"),
            (RegistryHive.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"),
            (RegistryHive.CurrentUser, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}"),
            (RegistryHive.CurrentUser, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}")
        };

        foreach (var (hive, path) in locations)
        {
            var pv = RegistryHelper.GetString(hive, path, "pv");
            if (!string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0")
                return pv;
        }

        return null;
    }
}
