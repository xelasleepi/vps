using System.Diagnostics;
using System.Text;

using Setup.Core.Abstractions;
using Setup.Core.Models;

namespace Setup.Core.Services;

/// <summary>
/// Runs external processes (silent installers, powershell, reg.exe, sc.exe…)
/// with asynchronous output capture and timeout handling. Non-zero exit codes
/// never throw; on a timeout the process tree is killed and
/// <see cref="ProcessResult.TimedOut"/> is set. Unexpected exceptions are
/// converted into a failed <see cref="ProcessResult"/>.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger _logger;

    /// <summary>Creates the process runner.</summary>
    /// <param name="logger">Logger used for the Software/Install and Errors channels.</param>
    public ProcessRunner(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments = "",
        int timeoutSeconds = 600,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.Info($"Running: {fileName} {arguments}".TrimEnd(), LogCategory.Software);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var outputDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) outputDone.TrySetResult(true);
            else lock (stdout) { stdout.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) errorDone.TrySetResult(true);
            else lock (stderr) { stderr.AppendLine(e.Data); }
        };

        try
        {
            if (!process.Start())
            {
                stopwatch.Stop();
                return Failed(fileName, stopwatch.Elapsed, "Failed to start process.");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error($"Failed to start '{fileName}'.", ex);
            return Failed(fileName, stopwatch.Elapsed, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // Ensure the async stream readers have flushed their final data.
            await Task.WhenAll(outputDone.Task, errorDone.Task).ConfigureAwait(false);

            stopwatch.Stop();

            var result = new ProcessResult
            {
                ExitCode = SafeExitCode(process),
                StandardOutput = ReadBuffer(stdout),
                StandardError = ReadBuffer(stderr),
                TimedOut = false,
                Elapsed = stopwatch.Elapsed,
                FileName = fileName
            };

            LogResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

            TryKillTree(process);

            var result = new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = ReadBuffer(stdout),
                StandardError = timedOut
                    ? $"Process timed out after {timeoutSeconds}s and was terminated."
                    : "Process was cancelled and terminated.",
                TimedOut = timedOut,
                Elapsed = stopwatch.Elapsed,
                FileName = fileName
            };

            if (timedOut)
                _logger.Error($"'{fileName}' timed out after {timeoutSeconds}s; process tree killed.");
            else
                _logger.Warning($"'{fileName}' cancelled; process tree killed.", LogCategory.Software);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TryKillTree(process);
            _logger.Error($"Error running '{fileName}'.", ex);
            return Failed(fileName, stopwatch.Elapsed, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunPowerShellAsync(
        string script,
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        // Encode the script as UTF-16LE Base64 to avoid all quoting problems.
        var bytes = Encoding.Unicode.GetBytes(script ?? string.Empty);
        var encoded = Convert.ToBase64String(bytes);

        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        return RunAsync("powershell.exe", args, timeoutSeconds, workingDirectory: null, cancellationToken);
    }

    private void LogResult(ProcessResult result)
    {
        if (result.Succeeded)
            _logger.Success(result.Summary, LogCategory.Software);
        else
            _logger.Error(result.Summary);
    }

    private static ProcessResult Failed(string fileName, TimeSpan elapsed, string error) => new()
    {
        ExitCode = -1,
        StandardOutput = string.Empty,
        StandardError = error,
        TimedOut = false,
        Elapsed = elapsed,
        FileName = fileName
    };

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string ReadBuffer(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString().TrimEnd('\r', '\n');
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have already exited.
        }
    }
}
