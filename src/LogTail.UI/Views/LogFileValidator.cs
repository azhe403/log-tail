namespace LogTail.UI.Views;

/// <summary>
/// Validation helpers for log file paths (drag-and-drop, file picker).
/// Lives in the Views namespace because it's a UX-level concern — the View
/// translates failure into a user-facing status message. Unit tests target
/// this class directly rather than the MainWindow code-behind.
/// </summary>
public static class LogFileValidator
{
    /// <summary>
    /// Returns the lower-case extension if it's one we open as a log file
    /// (<c>.log</c> or <c>.txt</c>); otherwise <c>null</c>.
    /// </summary>
    public static string? GetSupportedExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        // .jsonl joins .log and .txt: the workspace sidebar lists it, and a
        // listed extension that cannot be restored is a trap for the user.
        return ext is ".log" or ".txt" or ".jsonl" ? ext : null;
    }

    /// <summary>
    /// Validates a path before opening it as a log tab.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the file is openable.
    /// On <c>false</c>: <paramref name="error"/> is non-null when the failure
    /// must be reported to the user (file not found, access denied, locked),
    /// and <c>null</c> when the path is silently ignored (folder, unsupported
    /// extension) per spec.
    /// </returns>
    public static bool TryValidateFile(string path, out string? error)
    {
        // Folders: File.Exists returns false for them, so check Directory first
        // to get a clean silent-ignore branch (per spec).
        if (System.IO.Directory.Exists(path))
        {
            error = null;
            return false;
        }

        if (!System.IO.File.Exists(path))
        {
            error = $"Error: File not found - {path}";
            return false;
        }

        try
        {
            var attrs = System.IO.File.GetAttributes(path);
            if ((attrs & System.IO.FileAttributes.Directory) == System.IO.FileAttributes.Directory)
            {
                error = null;
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            error = $"Error: Cannot read file - access denied: {path}";
            return false;
        }
        catch (System.IO.IOException ex)
        {
            error = $"Error: File is locked - {ex.Message}";
            return false;
        }

        if (GetSupportedExtension(path) == null)
        {
            error = null;
            return false;
        }

        // Open-and-close to surface permission issues. Must use the same
        // FileShare flags as FileTailSource (ReadWrite | Delete) so we don't
        // false-positive on log files that are actively being written to by
        // another process — that's the normal case for a log tailer.
        try
        {
            using var stream = System.IO.File.Open(
                path,
                System.IO.FileMode.Open,
                System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            error = $"Error: Cannot read file - access denied: {path}";
            return false;
        }
        catch (System.IO.IOException ex)
        {
            error = $"Error: Cannot open file - {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }
}
