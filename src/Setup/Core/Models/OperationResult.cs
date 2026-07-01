namespace Setup.Core.Models;

/// <summary>
/// The result of a single deployment operation (an install, an optimization
/// step, a cleanup task…). Every operation reports SUCCESS / FAILED / SKIPPED
/// together with the elapsed time, and failures continue the deployment.
/// </summary>
public sealed class OperationResult
{
    /// <summary>Human-readable name of the operation, e.g. "WinRAR".</summary>
    public string Name { get; init; } = "";

    /// <summary>Final status of the operation.</summary>
    public OperationStatus Status { get; init; }

    /// <summary>Optional detail (reason for skip, error text, version installed…).</summary>
    public string? Message { get; init; }

    /// <summary>Wall-clock time the operation took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The underlying exception, when the operation failed.</summary>
    public Exception? Error { get; init; }

    public bool IsSuccess => Status == OperationStatus.Success;
    public bool IsFailure => Status == OperationStatus.Failed;
    public bool IsSkipped => Status == OperationStatus.Skipped;

    public static OperationResult Success(string name, TimeSpan elapsed, string? message = null)
        => new() { Name = name, Status = OperationStatus.Success, Elapsed = elapsed, Message = message };

    public static OperationResult Failed(string name, TimeSpan elapsed, string? message = null, Exception? error = null)
        => new() { Name = name, Status = OperationStatus.Failed, Elapsed = elapsed, Message = message, Error = error };

    public static OperationResult Skipped(string name, TimeSpan elapsed, string? message = null)
        => new() { Name = name, Status = OperationStatus.Skipped, Elapsed = elapsed, Message = message };

    /// <summary>Short "SUCCESS in 1.2s" style string for logs and the summary screen.</summary>
    public string StatusLine()
    {
        var verb = Status switch
        {
            OperationStatus.Success => "SUCCESS",
            OperationStatus.Failed => "FAILED",
            OperationStatus.Skipped => "SKIPPED",
            OperationStatus.InProgress => "RUNNING",
            _ => "PENDING"
        };
        var detail = string.IsNullOrWhiteSpace(Message) ? "" : $" — {Message}";
        return $"{Name}: {verb} ({Elapsed.TotalSeconds:0.0}s){detail}";
    }
}
