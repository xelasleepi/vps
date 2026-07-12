using Microsoft.Win32;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Installs the .NET Framework 4.8 web installer silently (<c>/q /norestart</c>).
/// Detection uses the canonical NDP release DWORD: a value of ≥ 528040 under
/// <c>NDP\v4\Full</c> indicates 4.8 (or newer) is present. Exit code 3010 is
/// handled as success with a pending reboot.
/// </summary>
public sealed class DotNetFrameworkInstaller : InstallerBase
{
    /// <summary>Minimum <c>Release</c> value that indicates .NET Framework 4.8.</summary>
    private const int NetFx48Release = 528040;

    private const string NdpKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

    /// <inheritdoc/>
    public override string Name => ".NET Framework 4.8";

    /// <inheritdoc/>
    public override string Key => "dotnetfx48";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallDotNet;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.DotNetFramework48;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var release = RegistryHelper.GetDword(RegistryHive.LocalMachine, NdpKey, "Release");
        return Task.FromResult(release is int r && r >= NetFx48Release);
    }

    /// <summary>Verifies by re-checking the NDP release value.</summary>
    protected override Task<bool> VerifyAsync(DeploymentContext context, string installerPath, CancellationToken ct)
        => IsInstalledAsync(context, ct);
}
