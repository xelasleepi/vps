using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// Runs external processes (silent installers, powershell, reg.exe, sc.exe…)
/// with output capture and timeout handling. Implementations never throw for
/// non-zero exit codes; callers inspect <see cref="ProcessResult"/>.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs an executable and waits for it to exit (or time out).</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        string arguments = "",
        int timeoutSeconds = 600,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a PowerShell script via <c>powershell.exe -NoProfile -NonInteractive
    /// -ExecutionPolicy Bypass</c>. The script text is passed safely (base64
    /// encoded command) to avoid quoting problems.
    /// </summary>
    Task<ProcessResult> RunPowerShellAsync(
        string script,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default);
}
