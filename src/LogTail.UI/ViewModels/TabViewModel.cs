using LogTail.Core.Models;
using LogTail.UI.Collections;
using ReactiveUI;

namespace LogTail.UI.ViewModels;

public sealed class TabViewModel : ReactiveObject
{
    private const int MaxLineLength = 4000;
    private string _fileName = string.Empty;
    private string _filePath = string.Empty;
    private string _status = "Idle";
    private bool _isLoading;
    private int _lineCount;
    private long _fileSize;
    private DateTime _lastModified;
    private double _linesPerSecond;
    private bool _isTailing;

    public string FileName
    {
        get => _fileName;
        set => this.RaiseAndSetIfChanged(ref _fileName, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => this.RaiseAndSetIfChanged(ref _filePath, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            IsLoading = string.Equals(value, "loading", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public BulkObservableCollection<EnrichedLogEvent> LogEvents { get; } = new();

    private long _totalLinesAppended;
    public long TotalLinesAppended => _totalLinesAppended;

    public int LineCount
    {
        get => _lineCount;
        set => this.RaiseAndSetIfChanged(ref _lineCount, value);
    }

    public long FileSize
    {
        get => _fileSize;
        set => this.RaiseAndSetIfChanged(ref _fileSize, value);
    }

    public DateTime LastModified
    {
        get => _lastModified;
        set => this.RaiseAndSetIfChanged(ref _lastModified, value);
    }

    public double LinesPerSecond
    {
        get => _linesPerSecond;
        set => this.RaiseAndSetIfChanged(ref _linesPerSecond, value);
    }

    public bool IsTailing
    {
        get => _isTailing;
        set => this.RaiseAndSetIfChanged(ref _isTailing, value);
    }

    /// <summary>
    /// True when this tab represents an open workspace folder instead of a
    /// log file. Folder tabs hold a <see cref="Preview"/> when a file has been
    /// picked from the sidebar, and never tail on their own account.
    /// </summary>
    public bool IsFolder { get; }

    /// <summary>
    /// The log file shown inside a folder tab, set by a single click in the
    /// sidebar. Null means the tab shows its workspace summary.
    /// </summary>
    public TabViewModel? Preview { get; private set; }

    public void SetPreview(TabViewModel? preview)
    {
        Preview = preview;

        // Both names matter: ShowsLog decides between the summary and the log
        // view, LogSource is what the log view binds its ItemsSource to. Raising
        // only ShowsLog leaves the view bound to the previous collection.
        this.RaisePropertyChanged(nameof(ShowsLog));
        this.RaisePropertyChanged(nameof(LogSource));
    }

    /// <summary>
    /// The log view to render for this tab: the preview inside a folder tab,
    /// otherwise the tab itself. A folder tab with no preview has no lines, so
    /// binding here is harmless while <see cref="ShowsLog"/> is false.
    /// </summary>
    public TabViewModel LogSource => Preview ?? this;

    /// <summary>
    /// True when this tab shows log lines: a log tab always, a folder tab only
    /// while a file is previewed inside it. The view uses it to pick between
    /// the log view and the workspace summary.
    /// </summary>
    public bool ShowsLog => !IsFolder || Preview is not null;

    public TabViewModel(string filePath, bool isFolder = false)
    {
        IsFolder = isFolder;
        FilePath = filePath;
        FileName = isFolder ? GetFolderLabel(filePath) : System.IO.Path.GetFileName(filePath);

        if (isFolder)
        {
            // Nothing to stat: a folder has no size or modified time to report.
            return;
        }

        // Best-effort: populate initial size and modified time so the status
        // bar shows something useful before tailing starts. Swallow IO errors
        // — validation already happened upstream; the tailing source will
        // surface any real problems when it actually opens the file.
        try
        {
            var info = new System.IO.FileInfo(filePath);
            if (info.Exists)
            {
                _fileSize = info.Length;
                _lastModified = info.LastWriteTime;
            }
        }
        catch (System.IO.IOException)
        {
            // Leave defaults (0 / default(DateTime)) — BuildTabStatus will
            // render these as "0 B" and empty time, not crash.
        }
        catch (System.UnauthorizedAccessException)
        {
            // Same: skip.
        }
    }

    /// <summary>
    /// Points this log tab at another file and clears the previous one's lines.
    /// A log tab is a viewer slot rather than a pinned file: a single click in
    /// the workspace sidebar retargets the tab that is on screen.
    /// </summary>
    public void LoadFile(string filePath)
    {
        FilePath = filePath;
        FileName = IsFolder
            ? GetFolderLabel(filePath)
            : System.IO.Path.GetFileName(filePath);

        LogEvents.Clear();
        _totalLinesAppended = 0;
        LineCount = 0;
        LinesPerSecond = 0;
        FileSize = 0;
        LastModified = default;
        Status = "Idle";

        // Best-effort, same as the constructor: a stat failure must not stop
        // the tailing source from opening the file a moment later.
        try
        {
            var info = new System.IO.FileInfo(filePath);
            if (info.Exists)
            {
                FileSize = info.Length;
                LastModified = info.LastWriteTime;
            }
        }
        catch (System.IO.IOException)
        {
        }
        catch (System.UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Display label for a folder tab: the folder name plus its parent, so two
    /// workspaces called "app" (say <c>logs\app</c> and <c>archive\app</c>) can
    /// be told apart in the tab strip, the sidebar and the status bar. A folder
    /// at a filesystem root has no parent to show, so it keeps its own path.
    /// </summary>
    private static string GetFolderLabel(string folderPath)
    {
        var trimmed = folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(name))
        {
            return folderPath;
        }

        // Purely path-based: no filesystem call, so this cannot fail or block.
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(trimmed));
        return string.IsNullOrEmpty(parent) ? name : $"{name} ({parent})";
    }

    public void AddLogEvent(EnrichedLogEvent logEvent)
    {
        _totalLinesAppended++;
        var trimmed = TrimLineIfNeeded(logEvent);
        var numberedEvent = trimmed with { LineNumber = (int)_totalLinesAppended };
        LogEvents.Add(numberedEvent);
        LineCount = LogEvents.Count;
    }

    /// <summary>
    /// Append a batch with a single UI notification. Assigns line numbers
    /// sequentially and caps absurdly long lines so layout stays cheap.
    /// </summary>
    public void AddLogEvents(IList<EnrichedLogEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        var numbered = new List<EnrichedLogEvent>(batch.Count);
        foreach (var item in batch)
        {
            _totalLinesAppended++;
            numbered.Add(TrimLineIfNeeded(item) with { LineNumber = (int)_totalLinesAppended });
        }

        LogEvents.AddRange(numbered);
        LineCount = LogEvents.Count;
    }

    /// <summary>
    /// Evict count oldest items with a single UI notification.
    /// </summary>
    public void EvictFromFront(int count)
    {
        if (count <= 0)
        {
            return;
        }

        LogEvents.RemoveFromFront(count);
        LineCount = LogEvents.Count;
    }

    public void ClearEvents()
    {
        LogEvents.Clear();
        LineCount = 0;
        _totalLinesAppended = 0;
    }

    private static EnrichedLogEvent TrimLineIfNeeded(EnrichedLogEvent logEvent)
    {
        var line = logEvent.Raw.Line;
        if (line.Length <= MaxLineLength)
        {
            return logEvent;
        }

        var trimmedRaw = logEvent.Raw with { Line = string.Concat(line.AsSpan(0, MaxLineLength), "…") };
        return logEvent with { Raw = trimmedRaw };
    }
}
