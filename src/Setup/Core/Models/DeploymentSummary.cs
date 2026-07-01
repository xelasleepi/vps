namespace Setup.Core.Models;

/// <summary>
/// Aggregated results of the whole deployment, rendered on the final screen and
/// written to the logs. Populated by the deployment engine as it runs.
/// </summary>
public sealed class DeploymentSummary
{
    /// <summary>Software that was installed this run.</summary>
    public List<OperationResult> Installed { get; } = new();

    /// <summary>Software skipped because it was already present.</summary>
    public List<OperationResult> Skipped { get; } = new();

    /// <summary>Any operation (install, optimization, cleanup) that failed.</summary>
    public List<OperationResult> Failed { get; } = new();

    /// <summary>Optimization steps that were applied (success + skipped).</summary>
    public List<OperationResult> Optimizations { get; } = new();

    /// <summary>Total wall-clock time of the deployment.</summary>
    public TimeSpan TotalElapsed { get; set; }

    /// <summary>Absolute path to the logs folder.</summary>
    public string LogDirectory { get; set; } = "";

    /// <summary>Whether an automatic reboot was scheduled at the end.</summary>
    public bool RebootScheduled { get; set; }

    /// <summary>Records a result into the appropriate bucket.</summary>
    public void Record(OperationResult result, bool isOptimization = false)
    {
        if (isOptimization)
            Optimizations.Add(result);

        switch (result.Status)
        {
            case OperationStatus.Success when !isOptimization:
                Installed.Add(result);
                break;
            case OperationStatus.Skipped when !isOptimization:
                Skipped.Add(result);
                break;
            case OperationStatus.Failed:
                Failed.Add(result);
                break;
        }
    }

    public int TotalOperations => Installed.Count + Skipped.Count + Failed.Count + Optimizations.Count;
    public bool HasFailures => Failed.Count > 0;
}
