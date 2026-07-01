using System.IO.Compression;

namespace Setup.Core.Utils;

/// <summary>
/// Exception-safe, best-effort file-system helpers shared across the app
/// (cleanup tasks, archive extraction, byte formatting). Every operation
/// swallows expected IO errors and returns a success flag or a partial result;
/// locked/in-use files are skipped rather than throwing.
/// </summary>
public static class FileSystemUtil
{
    /// <summary>
    /// Deletes a file, clearing the read-only attribute first. Returns true when
    /// the file no longer exists afterwards (including when it was already absent).
    /// </summary>
    public static bool TryDeleteFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return true;

            ClearReadOnly(path);
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a directory, best-effort. Clears read-only attributes on contained
    /// files. Returns true when the directory no longer exists afterwards.
    /// </summary>
    /// <param name="path">Directory to delete.</param>
    /// <param name="recursive">Delete contained files/subdirectories too.</param>
    public static bool TryDeleteDirectory(string path, bool recursive = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return true;

            if (recursive)
                ClearReadOnlyRecursive(path);

            Directory.Delete(path, recursive);
            return true;
        }
        catch
        {
            // Fall back to emptying the directory so at least the contents go away.
            try
            {
                TryEmptyDirectory(path, out _);
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, recursive: false);
                    return true;
                }
            }
            catch
            {
                // Ignore — reported via the return value.
            }

            return false;
        }
    }

    /// <summary>
    /// Deletes the <em>contents</em> of a directory (files and subdirectories) but
    /// keeps the directory itself. Locked files are skipped. Returns the number of
    /// bytes freed; <paramref name="filesDeleted"/> receives the count of files
    /// removed.
    /// </summary>
    public static long TryEmptyDirectory(string path, out int filesDeleted)
    {
        long bytesFreed = 0;
        filesDeleted = 0;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return 0;

        // Delete files first (record freed bytes), then prune empty subdirectories.
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return bytesFreed;
        }

        foreach (var file in files)
        {
            try
            {
                long size = 0;
                try { size = new FileInfo(file).Length; }
                catch { /* size unknown */ }

                ClearReadOnly(file);
                File.Delete(file);
                bytesFreed += size;
                filesDeleted++;
            }
            catch
            {
                // Locked/in-use file — skip it.
            }
        }

        // Remove now-empty subdirectories (deepest first).
        try
        {
            var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length)
                .ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: false);
                }
                catch
                {
                    // Non-empty (locked file remained) or in-use — skip.
                }
            }
        }
        catch
        {
            // Ignore enumeration failures.
        }

        return bytesFreed;
    }

    /// <summary>
    /// Extracts a ZIP archive to a destination directory. Creates the destination
    /// if needed and (by default) overwrites existing files. Returns success.
    /// </summary>
    /// <param name="zipPath">Path to the .zip archive.</param>
    /// <param name="destinationDir">Directory to extract into.</param>
    /// <param name="overwrite">Overwrite files that already exist.</param>
    public static bool TryExtractZip(string zipPath, string destinationDir, bool overwrite = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                return false;

            Directory.CreateDirectory(destinationDir);
            ZipFile.ExtractToDirectory(zipPath, destinationDir, overwriteFiles: overwrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Formats a byte count as a human-readable size with one decimal place
    /// (e.g. <c>4.2 MB</c>). Uses binary (1024) units B/KB/MB/GB/TB/PB.
    /// </summary>
    public static string HumanBytes(long bytes)
    {
        if (bytes < 0)
            return "-" + HumanBytes(-bytes);

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Bytes are whole numbers; larger units get one decimal place.
        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.0} {units[unit]}";
    }

    private static void ClearReadOnly(string file)
    {
        try
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static void ClearReadOnlyRecursive(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                ClearReadOnly(file);
        }
        catch
        {
            // Best-effort.
        }
    }
}
