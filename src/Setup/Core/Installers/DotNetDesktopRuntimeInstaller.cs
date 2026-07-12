using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Installers;

/// <summary>
/// Installs the .NET Desktop Runtime 8 (x64) silently
/// (<c>/install /quiet /norestart</c>). Detection checks for an
/// <c>8.*</c> folder under
/// <c>%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App</c>, falling
/// back to parsing <c>dotnet --list-runtimes</c> for a
/// <c>Microsoft.WindowsDesktop.App 8.</c> entry.
/// </summary>
public sealed class DotNetDesktopRuntimeInstaller : InstallerBase
{
    private const string DesktopAppRelative = @"dotnet\shared\Microsoft.WindowsDesktop.App";

    /// <inheritdoc/>
    public override string Name => ".NET Desktop Runtime 8 (x64)";

    /// <inheritdoc/>
    public override string Key => "dotnetdesktop8";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallDotNet;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.DotNetDesktopRuntime;

    /// <inheritdoc/>
    public override async Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        if (SharedFolderHasVersion8())
            return true;

        // Fall back to the runtime listing.
        return await ListRuntimesHasDesktop8Async(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> VerifyAsync(DeploymentContext context, string installerPath, CancellationToken ct)
        => IsInstalledAsync(context, ct);

    /// <summary>True when an <c>8.*</c> shared-framework folder exists in Program Files.</summary>
    private static bool SharedFolderHasVersion8()
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var dir = Path.Combine(programFiles, DesktopAppRelative);
            if (!Directory.Exists(dir))
                return false;

            return Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)
                .Any(n => n is not null && n.StartsWith("8.", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs <c>dotnet --list-runtimes</c> and looks for a WindowsDesktop 8 entry.</summary>
    private static async Task<bool> ListRuntimesHasDesktop8Async(DeploymentContext context, CancellationToken ct)
    {
        try
        {
            var result = await context.Process.RunAsync("dotnet", "--list-runtimes", 30, null, ct).ConfigureAwait(false);
            if (result.TimedOut)
                return false;

            var output = result.StandardOutput ?? "";
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Microsoft.WindowsDesktop.App 8.", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // dotnet not on PATH ⇒ not installed.
        }

        return false;
    }
}
