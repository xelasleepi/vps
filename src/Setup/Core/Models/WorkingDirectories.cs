namespace Setup.Core.Models;

/// <summary>
/// Resolved set of working folders. Created automatically at startup.
/// Layout (relative to <see cref="Root"/>):
/// <code>
///   downloads\   installer payloads
///   logs\        install / errors / downloads / optimization / software logs
///   temp\        scratch space (extracted archives, DirectX payload, …)
/// </code>
/// </summary>
public sealed class WorkingDirectories
{
    public required string Root { get; init; }
    public required string Downloads { get; init; }
    public required string Logs { get; init; }
    public required string Temp { get; init; }

    /// <summary>
    /// Builds the directory set. When <paramref name="root"/> is null/empty the
    /// folders are created next to the running executable under
    /// <c>SetupDeploy\</c>.
    /// </summary>
    public static WorkingDirectories Create(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            var baseDir = AppContext.BaseDirectory;
            root = Path.Combine(baseDir, "SetupDeploy");
        }

        root = Path.GetFullPath(root);
        var dirs = new WorkingDirectories
        {
            Root = root,
            Downloads = Path.Combine(root, "downloads"),
            Logs = Path.Combine(root, "logs"),
            Temp = Path.Combine(root, "temp")
        };
        dirs.EnsureCreated();
        return dirs;
    }

    /// <summary>Creates all folders if they do not already exist (idempotent).</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Downloads);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
    }
}
