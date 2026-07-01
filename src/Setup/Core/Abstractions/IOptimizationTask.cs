using Setup.Core.Deployment;
using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// A single, self-contained Windows optimization step (disable a service, apply
/// a registry tweak, set the power plan, clean a folder…). Implementations must
/// be idempotent and must never throw — they return an
/// <see cref="OperationResult"/> and the optimizer keeps going.
/// </summary>
public interface IOptimizationTask
{
    /// <summary>Human-readable name, e.g. "Disable SysMain".</summary>
    string Name { get; }

    /// <summary>Log channel for this task (defaults to Optimization).</summary>
    LogCategory Category => LogCategory.Optimization;

    /// <summary>Applies the optimization and reports the outcome.</summary>
    Task<OperationResult> ApplyAsync(DeploymentContext context, CancellationToken cancellationToken = default);
}
