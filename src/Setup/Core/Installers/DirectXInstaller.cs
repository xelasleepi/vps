using System.Diagnostics;
using Setup.Core.Deployment;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Installers;

/// <summary>
/// Installs the DirectX End-User Runtime (June 2010). The download is a
/// self-extracting archive, so installation is a two-step process:
/// <list type="number">
///   <item>Extract the payload to a temp folder via <c>installer.exe /Q /T:&lt;dir&gt; /C</c>.</item>
///   <item>Run <c>DXSETUP.exe /silent</c> from that folder.</item>
/// </list>
/// Detection heuristic: both the 32-bit and 64-bit <c>d3dx9_43.dll</c> exist in
/// the system directories, indicating the June 2010 runtime is present.
/// </summary>
public sealed class DirectXInstaller : InstallerBase
{
    /// <inheritdoc/>
    public override string Name => "DirectX End-User Runtime (June 2010)";

    /// <inheritdoc/>
    public override string Key => "directx";

    /// <inheritdoc/>
    public override bool IsEnabled(AppConfig config) => config.Features.InstallDirectX;

    /// <inheritdoc/>
    protected override SoftwareItem GetItem(AppConfig config) => config.Software.DirectX;

    /// <inheritdoc/>
    public override Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(D3dx9Present());

    /// <summary>Custom two-phase install: extract self-extractor, then run DXSETUP.</summary>
    public override async Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        context.Reporter.TrackItem(Key, Name, OperationStatus.InProgress);
        context.Reporter.SetCurrentTask($"Installing {Name}…");

        string? extractDir = null;
        try
        {
            if (await IsInstalledAsync(context, cancellationToken).ConfigureAwait(false))
                return Skip(context, sw, $"{Name} already installed");

            var item = GetItem(context.Config);

            var download = await DownloadItemAsync(context, item, cancellationToken).ConfigureAwait(false);
            if (!download.Success)
                return Fail(context, sw, $"{Name} download failed: {download.ErrorMessage}");

            // 1) Extract the self-extractor into a clean temp subfolder.
            extractDir = Path.Combine(context.Directories.Temp, "directx");
            FileSystemUtil.TryDeleteDirectory(extractDir);
            Directory.CreateDirectory(extractDir);

            context.Logger.Info($"Extracting DirectX payload to {extractDir}…", LogCategory.Software);
            var extract = await context.Process.RunAsync(
                download.FilePath, $"/Q /T:\"{extractDir}\" /C", 300, context.Directories.Downloads, cancellationToken).ConfigureAwait(false);

            if (!extract.Succeeded)
                return Fail(context, sw, $"DirectX extraction failed: {extract.Summary}");

            // 2) Run DXSETUP silently from the extracted payload.
            var dxSetup = Path.Combine(extractDir, "DXSETUP.exe");
            if (!File.Exists(dxSetup))
                return Fail(context, sw, "DXSETUP.exe not found after extraction");

            context.Logger.Info("Running DXSETUP.exe /silent…", LogCategory.Software);
            var setup = await context.Process.RunAsync(dxSetup, "/silent", 600, extractDir, cancellationToken).ConfigureAwait(false);

            if (!setup.Succeeded)
                return Fail(context, sw, $"DXSETUP failed: {setup.Summary}");

            if (!D3dx9Present())
                return Fail(context, sw, "DXSETUP completed but d3dx9_43.dll was not found");

            return Ok(context, sw, setup.RebootRequired ? "installed (reboot pending)" : "installed");
        }
        catch (Exception ex)
        {
            return Fail(context, sw, $"{Name} install error: {ex.Message}", ex);
        }
        finally
        {
            // Best-effort cleanup of the extracted payload.
            if (extractDir is not null)
                FileSystemUtil.TryDeleteDirectory(extractDir);
        }
    }

    /// <summary>
    /// Heuristic presence check: both native and WOW64 copies of
    /// <c>d3dx9_43.dll</c> exist (the last D3DX9 shipped in the June 2010 runtime).
    /// </summary>
    private static bool D3dx9Present()
        => FileExistsExpanded(@"%SystemRoot%\System32\d3dx9_43.dll")
           && FileExistsExpanded(@"%SystemRoot%\SysWOW64\d3dx9_43.dll");
}
