using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// A single software installer. Implementations are idempotent: they detect an
/// existing installation and skip, download from the configured source, install
/// silently, and verify. They never throw — failures are returned as an
/// <see cref="OperationResult"/> so the deployment can continue.
/// </summary>
public interface IInstaller
{
    /// <summary>Display name, e.g. "WinRAR".</summary>
    string Name { get; }

    /// <summary>Stable key used by the UI checklist / progress tracking.</summary>
    string Key { get; }

    /// <summary>Whether this installer is enabled by the current configuration.</summary>
    bool IsEnabled(AppConfig config);

    /// <summary>Detects whether the software is already present (skip check).</summary>
    Task<bool> IsInstalledAsync(DeploymentContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the full install (download → silent install → verify). Returns a
    /// result with SUCCESS / SKIPPED / FAILED and the elapsed time.
    /// </summary>
    Task<OperationResult> InstallAsync(DeploymentContext context, CancellationToken cancellationToken = default);
}
