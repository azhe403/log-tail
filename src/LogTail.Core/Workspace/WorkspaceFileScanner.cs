using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LogTail.Core.Workspace;

public enum WorkspaceSortCriterion
{
    Name,
    Size,
    Date,
}

public enum WorkspaceSortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// One log file inside a workspace folder, with the two facts the sidebar shows
/// next to the name: how big it is and when it was last written.
/// </summary>
public sealed record WorkspaceFile(string Name, string Path, long Size, DateTime LastModified)
{
    public string SizeText => FormatSize(Size);

    /// <summary>Compact timestamp for a 260px row; the full one is the tooltip.</summary>
    public string ModifiedText => LastModified == default ? "—" : LastModified.ToString("MM-dd HH:mm");

    /// <summary>B/KB/MB/GB with one decimal above a kilobyte.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>
/// Lists the log files under a workspace folder.
///
/// Flat and sorted, on purpose: a folder tab is a workspace, and the left
/// sidebar is the navigator inside it — a hierarchy would add a level of
/// navigation for no gain, since switching folders is what tabs are for.
/// The walk is manual so one denied subfolder does not abort the scan, and so
/// a directory cycle can be cut.
/// </summary>
public static class WorkspaceFileScanner
{
    /// <summary>
    /// Upper bound on files listed for one workspace. The remainder is counted
    /// so the UI can say how many it is not showing.
    /// </summary>
    public const int MaxFiles = 1000;

    /// <summary>
    /// Backstop for a directory cycle the reparse-point check cannot see.
    /// Real log trees are far shallower than this.
    /// </summary>
    public const int MaxDepth = 64;

    private static readonly string[] LogExtensions = [".log", ".txt", ".jsonl"];

    /// <summary>
    /// Sorts a sequence of <see cref="WorkspaceFile"/> according to the criterion and direction.
    /// </summary>
    public static IEnumerable<WorkspaceFile> OrderFiles(
        IEnumerable<WorkspaceFile> files,
        WorkspaceSortCriterion criterion,
        WorkspaceSortDirection direction) =>
        criterion switch
        {
            WorkspaceSortCriterion.Name => direction == WorkspaceSortDirection.Ascending
                ? files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(f => f.Path, StringComparer.OrdinalIgnoreCase),
            WorkspaceSortCriterion.Size => direction == WorkspaceSortDirection.Ascending
                ? files.OrderBy(f => f.Size).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Size).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            WorkspaceSortCriterion.Date => direction == WorkspaceSortDirection.Ascending
                ? files.OrderBy(f => f.LastModified).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.LastModified).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files,
        };

    /// <summary>
    /// Scans <paramref name="root"/> for log files, sorted by name.
    /// </summary>
    /// <param name="overflow">
    /// How many matching files were not returned because of <see cref="MaxFiles"/>.
    /// </param>
    /// <param name="error">
    /// Null on success, otherwise why the scan was incomplete (root missing, or
    /// a subtree skipped for permissions).
    /// </param>
    public static IReadOnlyList<WorkspaceFile> Scan(string root, out int overflow, out string? error)
    {
        error = null;
        overflow = 0;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            error = $"Folder not found: {root}";
            return [];
        }

        var paths = new List<string>();
        Collect(root, depth: 0, paths, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref error);

        // Path breaks ties so two files with the same name in different
        // subfolders land in a stable order.
        var ordered = paths
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        overflow = Math.Max(0, ordered.Count - MaxFiles);

        return ordered
            .Take(MaxFiles)
            .Select(ToWorkspaceFile)
            .ToList();
    }

    private static WorkspaceFile ToWorkspaceFile(string path)
    {
        long size = 0;
        var modified = default(DateTime);

        // Best-effort: a file deleted between the walk and the stat must not
        // sink the whole listing.
        try
        {
            var info = new FileInfo(path);
            size = info.Exists ? info.Length : 0;
            modified = info.Exists ? info.LastWriteTime : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new WorkspaceFile(Path.GetFileName(path), path, size, modified);
    }

    private static void Collect(
        string directory,
        int depth,
        List<string> files,
        HashSet<string> visited,
        ref string? error)
    {
        if (depth > MaxDepth || !visited.Add(directory))
        {
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error ??= $"Skipped {directory}: {ex.Message}";
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (!Directory.Exists(entry))
                {
                    if (IsLogFile(entry))
                    {
                        files.Add(entry);
                    }

                    continue;
                }

                // Never follow a directory link: a link to an ancestor is the
                // one way a subtree can re-enter itself, and a visited-path
                // set cannot catch it because each hop is a distinct string.
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    continue;
                }

                Collect(entry, depth + 1, files, visited, ref error);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error ??= $"Skipped {entry}: {ex.Message}";
            }
        }
    }

    private static bool IsLogFile(string path) =>
        LogExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
