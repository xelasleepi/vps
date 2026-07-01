using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// UI-facing progress sink. The deployment engine and installers push state
/// through this interface; the WPF view-model implements it and marshals updates
/// onto the dispatcher. All members must be safe to call from background threads.
/// </summary>
public interface IProgressReporter
{
    /// <summary>Sets the current high-level phase (header / banner subtitle).</summary>
    void SetPhase(DeploymentPhase phase);

    /// <summary>Sets the "Current Task" line, e.g. "Installing Visual C++ 2015-2022 x64…".</summary>
    void SetCurrentTask(string task);

    /// <summary>Sets overall progress 0–100 for the main progress bar.</summary>
    void SetOverallProgress(double percent);

    /// <summary>Sets the file currently being installed (shown under the task line).</summary>
    void SetCurrentFile(string? fileName);

    /// <summary>Reports live download progress (bytes, speed, ETA).</summary>
    void ReportDownload(DownloadProgress progress);

    /// <summary>
    /// Adds or updates a tracked item in the checklist panel (the "Downloads:" /
    /// steps list). <paramref name="key"/> is a stable identifier; calling again
    /// with the same key updates the existing row's <paramref name="status"/>.
    /// </summary>
    void TrackItem(string key, string displayName, OperationStatus status);
}
