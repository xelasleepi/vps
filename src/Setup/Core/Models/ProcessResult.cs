namespace Setup.Core.Models;

/// <summary>Result of running an external process (installer, powershell, etc.).</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public bool TimedOut { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>The file that was executed (for diagnostics).</summary>
    public string FileName { get; init; } = "";

    /// <summary>
    /// True when the process exited with a code generally treated as success.
    /// Many Microsoft installers use 3010 (success, reboot required) and
    /// 1641 (success, reboot initiated); both are treated as success here.
    /// </summary>
    public bool Succeeded => !TimedOut && ExitCode is 0 or 3010 or 1641;

    /// <summary>True when the exit code specifically indicates a pending reboot.</summary>
    public bool RebootRequired => ExitCode is 3010 or 1641;

    public string Summary => TimedOut
        ? $"'{FileName}' timed out after {Elapsed.TotalSeconds:0}s"
        : $"'{FileName}' exited with code {ExitCode} in {Elapsed.TotalSeconds:0.0}s";
}
