using Setup.Core.Models;

namespace Setup.Core.Deployment;

/// <summary>
/// Orchestrates the full unattended deployment: optimize Windows, install the
/// configured software, clean up, and (optionally) reboot. Produces a
/// <see cref="DeploymentSummary"/> for the final screen and logs.
/// </summary>
public interface IDeploymentEngine
{
    Task<DeploymentSummary> RunAsync(CancellationToken cancellationToken = default);
}
