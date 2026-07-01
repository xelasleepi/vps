using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Installers;

/// <summary>
/// Installs WinRAR silently (<c>/S</c>) from the configured RARLab download.
/// Detection: the installed <c>WinRAR.exe</c> under Program Files, or an
/// uninstall entry whose DisplayName contains "WinRAR".
/// </summary>
public sealed class WinRarInstaller : InstallerBase
{
    /// <inheritdoc/>
    public override string Name => "WinRAR";

    /// <inheritdoc/>
    public override string Key => "winrar";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallWinRAR;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.WinRar;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var installed = FileExistsExpanded(@"%ProgramFiles%\WinRAR\WinRAR.exe")
            || FileExistsExpanded(@"%ProgramFiles(x86)%\WinRAR\WinRAR.exe")
            || UninstallKeyContains("WinRAR");
        return Task.FromResult(installed);
    }

    /// <summary>Verifies by confirming <c>WinRAR.exe</c> now exists on disk.</summary>
    protected override Task<bool> VerifyAsync(DeploymentContext context, string installerPath, CancellationToken ct)
        => Task.FromResult(
            FileExistsExpanded(@"%ProgramFiles%\WinRAR\WinRAR.exe")
            || FileExistsExpanded(@"%ProgramFiles(x86)%\WinRAR\WinRAR.exe"));
}
