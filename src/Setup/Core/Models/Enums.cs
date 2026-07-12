namespace Setup.Core.Models;

/// <summary>
/// Severity / semantic level of a log line. Drives the color used in the
/// terminal UI and the [TAG] prefix written to disk.
/// </summary>
public enum LogLevel
{
    /// <summary>Plain white output.</summary>
    Normal,
    /// <summary>Informational (blue).</summary>
    Info,
    /// <summary>Successful operation (green).</summary>
    Success,
    /// <summary>Warning or skipped operation (yellow).</summary>
    Warning,
    /// <summary>Error / failure (red).</summary>
    Error,
    /// <summary>Download-related output (cyan).</summary>
    Download
}

/// <summary>
/// Logical log channel. Each value maps to a dedicated file under <c>logs\</c>.
/// Every entry is additionally mirrored to <c>install.log</c> for a single
/// chronological transcript.
/// </summary>
public enum LogCategory
{
    /// <summary>General deployment flow — logs/install.log.</summary>
    Install,
    /// <summary>Failures only — logs/errors.log.</summary>
    Errors,
    /// <summary>Download manager activity — logs/downloads.log.</summary>
    Downloads,
    /// <summary>Windows optimization steps — logs/optimization.log.</summary>
    Optimization,
    /// <summary>Software installation steps — logs/software.log.</summary>
    Software
}

/// <summary>Outcome of a single deployment operation.</summary>
public enum OperationStatus
{
    /// <summary>Queued, not yet started.</summary>
    Pending,
    /// <summary>Currently running.</summary>
    InProgress,
    /// <summary>Completed successfully.</summary>
    Success,
    /// <summary>Attempted but failed. Deployment continues.</summary>
    Failed,
    /// <summary>Skipped because it was already present / not applicable.</summary>
    Skipped
}

/// <summary>High-level phase of the deployment, shown in the UI header.</summary>
public enum DeploymentPhase
{
    Initializing,
    Optimizing,
    Installing,
    Configuring,
    CleaningUp,
    Complete
}
