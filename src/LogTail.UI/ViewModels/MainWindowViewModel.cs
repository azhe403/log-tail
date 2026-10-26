using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LogTail.Core.Buffer;
using LogTail.Core.Diagnostics;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using LogTail.Core.Pipeline;
using LogTail.Core.Sources;
using LogTail.Core.Workspace;
using ReactiveUI;

namespace LogTail.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private string _windowTitle = "Log Tail";

    public string WindowTitle
    {
        get => _windowTitle;
        set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    private string? _currentFilePath;
    public string? CurrentFilePath
    {
        get => _currentFilePath;
        set => this.RaiseAndSetIfChanged(ref _currentFilePath, value);
    }

    private string _statusMessage = "No file open";
    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _autoScroll = true;
    public bool AutoScroll
    {
        get => _autoScroll;
        set => this.RaiseAndSetIfChanged(ref _autoScroll, value);
    }

    private TabViewModel? _selectedTab;
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            SyncActiveLogTab();
            // The folder sidebar is only relevant for a folder tab: switching to
            // a log (or no) tab hides it even when other folder tabs stay open.
            this.RaisePropertyChanged(nameof(HasWorkspaces));
            this.RaisePropertyChanged(nameof(IsSidebarVisible));
            this.RaisePropertyChanged(nameof(SidebarVisible));
            this.RaisePropertyChanged(nameof(ShowSidebar));
            this.RaisePropertyChanged(nameof(ShowFolderSidebar));
            this.RaisePropertyChanged(nameof(FolderSidebarVisible));
            this.RaisePropertyChanged(nameof(IsFolderTabSelected));
            this.RaisePropertyChanged(nameof(SelectedTabIsFolder));
        }
    }

    private TabViewModel? _activeLogTab;

    /// <summary>
    /// The tab whose log is on screen: the selected tab when it is a log tab,
    /// or the file previewed inside a folder tab. Null for a folder tab with
    /// nothing picked yet — that state shows the workspace summary.
    /// </summary>
    public TabViewModel? ActiveLogTab
    {
        get => _activeLogTab;
        private set => this.RaiseAndSetIfChanged(ref _activeLogTab, value);
    }

    private void SyncActiveLogTab()
    {
        ActiveLogTab = SelectedTab is { IsFolder: true } folder ? folder.Preview : SelectedTab;

        // Preview swaps route through here too, so the search's scope tab
        // follows a folder tab changing the file it displays. Guarded with ?
        // because the setter can fire before the ctor assigns Search.
        Search?.SetActiveTab(SelectedTab);
    }

    private readonly ObservableAsPropertyHelper<string> _selectedTabStatus;
    public string SelectedTabStatus => _selectedTabStatus.Value;

    private string _appResourceText = "CPU — · RAM —";

    /// <summary>
    /// The app's own CPU and memory for the status bar. Both are process-wide
    /// facts: every tab shares one process, so neither can be attributed to a
    /// single tab. Recomputed once a second by <see cref="SampleResourcesNow"/>.
    /// </summary>
    public string AppResourceText
    {
        get => _appResourceText;
        private set => this.RaiseAndSetIfChanged(ref _appResourceText, value);
    }

    private string _tabResourceText = "Read — · buf —";

    /// <summary>
    /// The tab on screen's share of the app's resources: how fast its file is
    /// being read, and how much memory its rendered buffer holds. "—" while
    /// nothing is tailed; see <see cref="SampleResourcesNow"/>.
    /// </summary>
    public string TabResourceText
    {
        get => _tabResourceText;
        private set => this.RaiseAndSetIfChanged(ref _tabResourceText, value);
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>
    /// Search state for the toolbar search box and the bottom results panel.
    /// Owned here so tabs flow into it, but self-contained: it receives the
    /// selected tab and an activate callback, not this view model.
    /// </summary>
    public SearchViewModel Search { get; }

    // --- Workspace (folder tabs) ---
    // A folder tab is a workspace; the left sidebar navigates the log files
    // inside it. ponytail: one watcher, not one per tab.
    public ObservableCollection<WorkspaceFile> WorkspaceFiles { get; } = new();

    /// <summary>
    /// True when the tab currently on screen is a folder (workspace) tab, so the
    /// folder sidebar should be shown. A log tab (or no tab) hides it, even when
    /// other folder tabs remain open — the navigator only makes sense for the
    /// folder being browsed. Kept as the binding target for the sidebar's
    /// <c>IsVisible</c> so existing callers and tests keep working.
    /// </summary>
    public bool HasWorkspaces => SelectedTab is { IsFolder: true };

    // Aliases kept for compatibility with callers/tests that name the sidebar's
    // visibility differently.
    public bool IsSidebarVisible => HasWorkspaces;
    public bool SidebarVisible => HasWorkspaces;
    public bool ShowSidebar => HasWorkspaces;
    public bool ShowFolderSidebar => HasWorkspaces;
    public bool FolderSidebarVisible => HasWorkspaces;
    public bool IsFolderTabSelected => HasWorkspaces;
    public bool SelectedTabIsFolder => HasWorkspaces;

    public bool IsSidebarOnRight => Settings.SidebarPosition == SidebarPosition.Right;

    public int SidebarColumn => IsSidebarOnRight ? 1 : 0;

    public int ContentColumn => IsSidebarOnRight ? 0 : 1;

    public Dock SidebarDock => IsSidebarOnRight ? Dock.Right : Dock.Left;

    public Thickness SidebarBorderThickness => IsSidebarOnRight
        ? new Thickness(1, 0, 0, 0)
        : new Thickness(0, 0, 1, 0);

    public string SidebarHintText => IsSidebarOnRight
        ? "Click a file in the right sidebar to view its log in this tab"
        : "Click a file in the left sidebar to view its log in this tab";

    private void NotifySidebarPositionChanged()
    {
        this.RaisePropertyChanged(nameof(IsSidebarOnRight));
        this.RaisePropertyChanged(nameof(SidebarColumn));
        this.RaisePropertyChanged(nameof(ContentColumn));
        this.RaisePropertyChanged(nameof(SidebarDock));
        this.RaisePropertyChanged(nameof(SidebarBorderThickness));
        this.RaisePropertyChanged(nameof(SidebarHintText));
    }

    public WorkspaceSortCriterion WorkspaceSortCriterion =>
        SelectedWorkspaceTab?.WorkspaceSortCriterion ?? WorkspaceSortCriterion.Name;

    public WorkspaceSortDirection WorkspaceSortDirection =>
        SelectedWorkspaceTab?.WorkspaceSortDirection ?? WorkspaceSortDirection.Ascending;

    public bool IsWorkspaceSortNameActive => WorkspaceSortCriterion == WorkspaceSortCriterion.Name;
    public bool IsWorkspaceSortSizeActive => WorkspaceSortCriterion == WorkspaceSortCriterion.Size;
    public bool IsWorkspaceSortDateActive => WorkspaceSortCriterion == WorkspaceSortCriterion.Date;

    public string WorkspaceSortNameLabel =>
        IsWorkspaceSortNameActive ? (WorkspaceSortDirection == WorkspaceSortDirection.Ascending ? "Name ↑" : "Name ↓") : "Name";

    public string WorkspaceSortSizeLabel =>
        IsWorkspaceSortSizeActive ? (WorkspaceSortDirection == WorkspaceSortDirection.Ascending ? "Size ↑" : "Size ↓") : "Size";

    public string WorkspaceSortDateLabel =>
        IsWorkspaceSortDateActive ? (WorkspaceSortDirection == WorkspaceSortDirection.Ascending ? "Date ↑" : "Date ↓") : "Date";

    private void NotifyWorkspaceSortChanged()
    {
        this.RaisePropertyChanged(nameof(WorkspaceSortCriterion));
        this.RaisePropertyChanged(nameof(WorkspaceSortDirection));
        this.RaisePropertyChanged(nameof(WorkspaceSortNameLabel));
        this.RaisePropertyChanged(nameof(WorkspaceSortSizeLabel));
        this.RaisePropertyChanged(nameof(WorkspaceSortDateLabel));
        this.RaisePropertyChanged(nameof(IsWorkspaceSortNameActive));
        this.RaisePropertyChanged(nameof(IsWorkspaceSortSizeActive));
        this.RaisePropertyChanged(nameof(IsWorkspaceSortDateActive));
    }

    public event Action? WorkspaceSortChanged;
    public event Action? WorkspacePreviewChanged;

    public void ToggleWorkspaceSort(WorkspaceSortCriterion criterion)
    {
        var folder = SelectedWorkspaceTab;
        if (folder is null)
        {
            return;
        }

        if (folder.WorkspaceSortCriterion == criterion)
        {
            folder.WorkspaceSortDirection = folder.WorkspaceSortDirection == WorkspaceSortDirection.Ascending
                ? WorkspaceSortDirection.Descending
                : WorkspaceSortDirection.Ascending;
        }
        else
        {
            folder.WorkspaceSortCriterion = criterion;
            // Defaults: Name -> Asc (A-Z), Size -> Desc (largest first), Date -> Desc (newest first)
            folder.WorkspaceSortDirection = criterion == WorkspaceSortCriterion.Name
                ? WorkspaceSortDirection.Ascending
                : WorkspaceSortDirection.Descending;
        }

        NotifyWorkspaceSortChanged();
        ApplyWorkspaceSort();
        WorkspaceSortChanged?.Invoke();
    }

    private void ApplyWorkspaceSort()
    {
        if (WorkspaceFiles.Count <= 1)
        {
            return;
        }

        var sorted = WorkspaceFileScanner.OrderFiles(WorkspaceFiles, WorkspaceSortCriterion, WorkspaceSortDirection).ToList();
        var selected = _selectedWorkspaceFile;
        WorkspaceFiles.Clear();
        foreach (var file in sorted)
        {
            WorkspaceFiles.Add(file);
        }

        if (selected is not null)
        {
            this.RaisePropertyChanged(nameof(SelectedWorkspaceFile));
        }
    }

    private int _workspaceOverflowCount;

    /// <summary>How many log files the last scan found beyond the 1000 it shows.</summary>
    public int WorkspaceOverflowCount
    {
        get => _workspaceOverflowCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _workspaceOverflowCount, value);
            this.RaisePropertyChanged(nameof(HasWorkspaceOverflow));
        }
    }

    /// <summary>
    /// Whether the sidebar's "N more files not shown" footer applies.
    /// A bool rather than testing the count in XAML: an int bound through a
    /// string converter is never "empty", so the footer would always show.
    /// </summary>
    public bool HasWorkspaceOverflow => WorkspaceOverflowCount > 0;

    /// <summary>
    /// Folder tabs, in tab order. A live collection rather than a computed
    /// property: handing a Selector a fresh List on every change makes it tear
    /// down its containers and loses the sidebar selection.
    /// </summary>
    internal ObservableCollection<TabViewModel> WorkspaceTabs { get; } = new();

    private TabViewModel? _selectedWorkspaceTab;

    /// <summary>
    /// The folder tab whose files the sidebar is listing. Selecting a log tab
    /// leaves it alone on purpose: the navigator stays available while reading
    /// a log, and switching folder tabs moves it.
    /// </summary>
    public TabViewModel? SelectedWorkspaceTab
    {
        get => _selectedWorkspaceTab;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedWorkspaceTab, value);
            this.RaisePropertyChanged(nameof(SelectedWorkspaceLabel));
            NotifyWorkspaceSortChanged();
        }
    }

    public string SelectedWorkspaceLabel => _selectedWorkspaceTab?.FileName ?? string.Empty;

    private FileSystemWatcher? _workspaceWatcher;
    private IDisposable? _workspaceDebounce;
    private string? _scannedFolder;

    /// <summary>
    /// The folder the watcher is currently aimed at. Kept apart from
    /// <see cref="_scannedFolder"/> because that one is only assigned when a
    /// scan lands, and deciding the watcher from it loses the watcher whenever
    /// the user returns to a folder before that folder's scan finished.
    /// </summary>
    private string? _watchedFolder;

    internal string? WatchedFolder => _watchedFolder;

    internal int RateSubscriptionCount => _rateSubscriptions.Count;

    /// <summary>
    /// Bumped per scan. A result whose generation is stale belongs to a folder
    /// the user has already navigated away from, and must not land.
    /// </summary>
    private int _scanGeneration;

    /// <summary>
    /// Aims the sidebar at the selected folder tab: rescans when the folder
    /// changed, and keeps the last folder listed when a log tab is selected.
    /// </summary>
    private void LoadWorkspaceFor(TabViewModel? tab)
    {
        if (tab is { IsFolder: true })
        {
            SelectedWorkspaceTab = tab;
        }
        else if (SelectedWorkspaceTab is { } stale && !Tabs.Contains(stale))
        {
            SelectedWorkspaceTab = Tabs.FirstOrDefault(t => t.IsFolder);
        }

        var folder = SelectedWorkspaceTab;
        if (folder is null)
        {
            StopWorkspaceWatcher();
            ClearWorkspaceFiles();
            return;
        }

        // Rescan only when the listed folder actually changed: selecting a log
        // tab must not re-walk the whole folder. The watcher is decided on its
        // own flag, so an A -> B -> A switch before B's scan lands still ends
        // up watching A again.
        if (!string.Equals(_scannedFolder, folder.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            RequestWorkspaceScan(folder);
        }

        if (!string.Equals(_watchedFolder, folder.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            StartWorkspaceWatcher(folder.FilePath, folder);
        }

        // Re-open the last file previewed in this folder, if any, so coming
        // back to a folder tab does not drop the user on the summary.
        if (folder.Preview is null &&
            _folderPreviewPaths.TryGetValue(folder.FilePath, out var remembered) &&
            !string.IsNullOrWhiteSpace(remembered))
        {
            _ = ShowWorkspaceFileAsync(remembered);
        }
    }

    private void ClearWorkspaceFiles()
    {
        _scanGeneration++;
        _scannedFolder = null;
        WorkspaceFiles.Clear();
        WorkspaceOverflowCount = 0;
    }

    /// <summary>
    /// Scans off the UI thread. A directory walk over a large workspace blocks
    /// the dispatcher for as long as it runs, and a blocked dispatcher means
    /// buffered log events never render — the log looks frozen even though the
    /// source is still tailing.
    /// </summary>
    private void RequestWorkspaceScan(TabViewModel folder)
    {
        var generation = ++_scanGeneration;
        var root = folder.FilePath;

        Task.Run(() =>
            {
                var files = WorkspaceFileScanner.Scan(root, out var overflow, out var error);
                return (Files: files, Overflow: overflow, Error: error);
            })
            .ContinueWith(
                task => Dispatcher.UIThread.Post(() =>
                {
                    // Staleness first: a scan for a folder the user already left
                    // must not touch the status bar, failed or not.
                    if (generation != _scanGeneration ||
                        !ReferenceEquals(SelectedWorkspaceTab, folder))
                    {
                        return;
                    }

                    if (task.IsFaulted || task.IsCanceled)
                    {
                        StatusMessage = $"Workspace scan failed: {task.Exception?.GetBaseException().Message}";
                        return;
                    }

                    var result = task.Result;
                    _scannedFolder = root;
                    WorkspaceOverflowCount = result.Overflow;
                    var selected = _selectedWorkspaceFile;
                    var ordered = WorkspaceFileScanner.OrderFiles(result.Files, WorkspaceSortCriterion, WorkspaceSortDirection);
                    WorkspaceFiles.Clear();
                    foreach (var file in ordered)
                    {
                        WorkspaceFiles.Add(file);
                    }

                    // Re-assert the selection: clearing the list makes the ListBox
                    // drop it, so the row would lose its highlight even though the
                    // same file is still the one on screen.
                    if (selected is not null)
                    {
                        this.RaisePropertyChanged(nameof(SelectedWorkspaceFile));
                    }
                    else if (SelectedTab is { IsFolder: true } folder && folder.Preview is { } preview)
                    {
                        UpdateSelectedWorkspaceFile(preview.FilePath);
                    }

                    StatusMessage = result.Error ?? $"Workspace: {folder.FileName}";
                }),
                TaskScheduler.Default);
    }

    private void StartWorkspaceWatcher(string root, TabViewModel folder)
    {
        StopWorkspaceWatcher();
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, Filter = "*.*" };
            var subject = new Subject<Unit>();
            var debounce = subject.Throttle(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => RequestWorkspaceScan(folder));
            FileSystemEventHandler h = (_, _) => subject.OnNext(Unit.Default);
            RenamedEventHandler rh = (_, _) => subject.OnNext(Unit.Default);
            watcher.Created += h; watcher.Deleted += h; watcher.Renamed += rh;
            // Raise events only once the handlers are attached, or a file
            // created in that window is missed with nothing to pick it up.
            watcher.EnableRaisingEvents = true;
            // The cleanup callback must close over the `watcher` local, never the
            // _workspaceWatcher field: StopWorkspaceWatcher nulls that field before
            // disposing this composite, so a field read here would NRE and take the
            // whole app down with it.
            _workspaceDebounce = new CompositeDisposable(debounce, Disposable.Create(() => { watcher.Created -= h; watcher.Deleted -= h; watcher.Renamed -= rh; subject.Dispose(); }));
            _workspaceWatcher = watcher;
            _watchedFolder = root;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Workspace watch: {ex.Message}";
        }
    }

    private void StopWorkspaceWatcher()
    {
        try
        {
            _workspaceWatcher?.Dispose();
        }
        catch (Exception ex)
        {
            // Swallowing this silently would hide a leaked OS handle, which is
            // the one failure here worth knowing about.
            _logger?.Warn($"Failed to dispose workspace watcher: {ex.Message}");
        }

        _workspaceWatcher = null;
        _watchedFolder = null;
        _workspaceDebounce?.Dispose();
        _workspaceDebounce = null;
    }

    /// <summary>
    /// Mirrors the folder tabs into <see cref="WorkspaceTabs"/> in place, so
    /// the sidebar's selection survives unrelated tab churn. Re-aims the
    /// sidebar when the folder it was listing is closed.
    /// </summary>
    private void SyncWorkspaceTabs()
    {
        var folderTabs = Tabs.Where(t => t.IsFolder).ToList();

        for (var i = WorkspaceTabs.Count - 1; i >= 0; i--)
        {
            if (!folderTabs.Contains(WorkspaceTabs[i]))
            {
                WorkspaceTabs.RemoveAt(i);
            }
        }

        for (var i = 0; i < folderTabs.Count; i++)
        {
            if (!WorkspaceTabs.Contains(folderTabs[i]))
            {
                WorkspaceTabs.Insert(Math.Min(i, WorkspaceTabs.Count), folderTabs[i]);
            }
        }

        if (SelectedWorkspaceTab is { } selected && !WorkspaceTabs.Contains(selected))
        {
            LoadWorkspaceFor(SelectedTab);
        }
    }

    public ReactiveCommand<Unit, Unit> OpenFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<TabViewModel, Unit> CloseTabCommand { get; }
    public ReactiveCommand<WorkspaceSortCriterion, Unit> SortCommand { get; }
    public SettingsViewModel Settings { get; }

    private readonly ObservableAsPropertyHelper<bool> _isTailing;
    public bool IsTailing => _isTailing.Value;

    public Interaction<Unit, string?> ShowOpenFileDialog { get; } = new();
    public Interaction<Unit, Unit> ShowSettingsDialog { get; } = new();

    private readonly SettingsStore _settings;
    private readonly ILogSourceFactory _sourceFactory;
    private readonly ILogTailLogger? _logger;
    private readonly RingBuffer<EnrichedLogEvent> _buffer;
    private ILogSource? _source;

    /// <summary>
    /// The tab the running source belongs to. <see cref="SelectedTab"/> cannot
    /// answer this: by the time the SelectedTab observer switches sources, the
    /// property already points at the new tab, so it would clear the wrong one
    /// and leave the tab you left claiming to be tailed.
    /// </summary>
    private TabViewModel? _tailingTab;

    public ILogSource? CurrentSource
    {
        get => _source;
        private set => this.RaiseAndSetIfChanged(ref _source, value);
    }

    private int _bufferCapacity;
    public int BufferCapacity
    {
        get => _bufferCapacity;
        private set => this.RaiseAndSetIfChanged(ref _bufferCapacity, value);
    }

    private IDisposable? _eventsSubscription;
    private Action? _onInitialLoadedHandler;
    private readonly Dictionary<TabViewModel, IDisposable> _rateSubscriptions = new();
    private readonly Dictionary<TabViewModel, Queue<DateTimeOffset>> _rateWindows = new();
    private readonly IDisposable _rateTimer;

    // Resource readouts. Process-level CPU/memory cannot be split per tab, so one
    // sampler serves the window; the per-tab facts come from the tab's own file
    // size and buffer instead.
    private readonly IProcessResourceSampler _resourceSampler;

    // Read-rate baseline: which tab and file the last sample measured, and the
    // file size it measured from. Re-established per tab — and per file, because
    // the sidebar retargets a tab at a new file without replacing the tab — so
    // one file's size is never charged to a second of reading another.
    private TabViewModel? _readRateTab;
    private string? _readRateBaselinePath;
    private long _readRateBaselineSize;

    public MainWindowViewModel(
        SettingsStore settings,
        ILogSourceFactory sourceFactory,
        ILogTailLogger? logger = null,
        SettingsViewModel? settingsViewModel = null,
        IProcessResourceSampler? resourceSampler = null)
    {
        _settings = settings;
        _sourceFactory = sourceFactory;
        _logger = logger;
        _resourceSampler = resourceSampler ?? new ProcessResourceSampler();
        Settings = settingsViewModel ?? new SettingsViewModel(settings, logger);
        Search = new SearchViewModel(Tabs, tab => SelectedTab = tab);

        // Restore settings.
        var loaded = _settings.Load();
        // Keep the shared buffer (and therefore the per-tab cap, BufferCapacity)
        // at the configured size: auto-growing up to MaxBufferCapacity let a
        // single large file balloon memory from ~50k to 2M lines, and the buffer
        // is not consumed by search/filter yet. Evict oldest instead of growing.
        var initial = loaded.BufferCapacity > 0 ? loaded.BufferCapacity : 50_000;
        var max = initial;
        _buffer = new RingBuffer<EnrichedLogEvent>(initial, max);
        _buffer.Grew += (_, newCapacity) =>
            Dispatcher.UIThread.Post(() => BufferCapacity = newCapacity);
        BufferCapacity = _buffer.Capacity;

        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        ClearCommand = ReactiveCommand.Create(Clear);
        CloseTabCommand = ReactiveCommand.Create<TabViewModel>(CloseTab);
        SortCommand = ReactiveCommand.Create<WorkspaceSortCriterion>(ToggleWorkspaceSort);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(
            async () => await ShowSettingsDialog.Handle(Unit.Default));

        // Wire IsTailing from the currently active source's IsRunning state.
        _isTailing = this.WhenAnyValue(x => x.CurrentSource)
            .Select(source => source?.IsRunning == true)
            .ObserveOn(RxApp.MainThreadScheduler)
            .ToProperty(this, x => x.IsTailing);

        // Compose the status bar summary for the log on screen (size, modified
        // time, lines-per-second, tailing state). Recomputes when any of those
        // change. BufferCapacity is global (one shared RingBuffer), so the
        // trigger re-fires when it changes too — though M1 only sets it once at
        // ctor. Tracks ActiveLogTab, so a folder tab's preview is described
        // rather than the folder itself.
        _selectedTabStatus = this.WhenAnyValue(
                x => x.ActiveLogTab,
                x => x.BufferCapacity,
                x => x.Search.MatchCountText)
            .Select(tuple => tuple.Item1 is null
                ? Observable.Return(string.Empty)
                : tuple.Item1.WhenAnyValue(
                        t => t.IsTailing,
                        t => t.FileSize,
                        t => t.LastModified,
                        t => t.LinesPerSecond,
                        t => t.TotalLineCount,
                        t => t.LogEvents.Count)
                    .Select(_ => BuildTabStatus(tuple.Item1, tuple.Item2, tuple.Item3)))
            .Switch()
            .ToProperty(this, x => x.SelectedTabStatus);

        // One-second housekeeping tick: decay the idle line rate to zero and take
        // the resource reading. One timer rather than one per concern — both jobs
        // are a once-a-second pass on the UI thread.
        _rateTimer = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                PruneRates();
                SyncLineCount();
                SampleResourcesNow();
            });

        // Seed the bar at once: this is the CPU baseline, so the first tick one
        // second later already has an interval to divide by.
        SampleResourcesNow();

        // Sidebar visibility and its folder list are computed from the tab
        // collection, so a tab add/remove has to re-announce them.
        Tabs.CollectionChanged += (_, _) =>
        {
            SyncWorkspaceTabs();
            this.RaisePropertyChanged(nameof(HasWorkspaces));
            this.RaisePropertyChanged(nameof(IsSidebarVisible));
            this.RaisePropertyChanged(nameof(SidebarVisible));
            this.RaisePropertyChanged(nameof(ShowSidebar));
            this.RaisePropertyChanged(nameof(ShowFolderSidebar));
            this.RaisePropertyChanged(nameof(FolderSidebarVisible));
            this.RaisePropertyChanged(nameof(IsFolderTabSelected));
            this.RaisePropertyChanged(nameof(SelectedTabIsFolder));
        };

        // Sidebar Left/Right applies instantly: Settings owns persistence,
        // this VM only re-announces the derived column/border/hint bindings.
        Settings.SidebarPositionChanged += _ => NotifySidebarPositionChanged();

        // Drive tailing from the selected tab: switching tabs stops the old source
        // and starts tailing the newly-selected file into that tab's LogEvents.
        // A folder tab tails nothing of its own — it tails ActiveLogTab, which is
        // the file previewed inside it, or null while it shows its summary.
        this.WhenAnyValue(x => x.SelectedTab)
            .Skip(1)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async tab =>
            {
                LoadWorkspaceFor(tab);

                // Derived from the emitted tab, not from ActiveLogTab: the
                // scheduler can run this inline while the SelectedTab setter is
                // still raising PropertyChanged, so the property has not been
                // synced yet at this point.
                var logTab = tab is { IsFolder: true } folder ? folder.Preview : tab;
                CurrentFilePath = logTab?.FilePath ?? tab?.FilePath;

                if (logTab is null)
                {
                    await StopCurrentSourceAsync();
                    StatusMessage = tab is null ? "No file open" : $"Workspace: {tab.FileName}";
                    return;
                }

                await StartTailingForAsync(logTab);
            });
    }

    /// <summary>
    /// Starts (or resumes) tailing into a log tab. A tab that already holds
    /// lines is resumed from the current end of file, so coming back to it does
    /// not re-read the whole file.
    /// </summary>
    private async Task StartTailingForAsync(TabViewModel logTab)
    {
        var resumeOnly = logTab.LogEvents.Count > 0;
        StatusMessage = $"Tailing: {Path.GetFileName(logTab.FilePath)}";

        try
        {
            await StartTailingAsync(logTab, resumeOnly);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private WorkspaceFile? _selectedWorkspaceFile;

    private void UpdateSelectedWorkspaceFile(string fullPath)
    {
        var match = WorkspaceFiles.FirstOrDefault(f =>
            string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (match is not null && !ReferenceEquals(_selectedWorkspaceFile, match))
        {
            _selectedWorkspaceFile = match;
            this.RaisePropertyChanged(nameof(SelectedWorkspaceFile));
        }
    }

    /// <summary>
    /// The file highlighted in the sidebar. Setting it shows the file, so a
    /// single click navigates through the VM however the selection arrived —
    /// pointer handler or binding. Null (what a list rebuild leaves behind) is
    /// ignored, so a watcher refresh cannot retarget the tab.
    /// </summary>
    public WorkspaceFile? SelectedWorkspaceFile
    {
        get => _selectedWorkspaceFile;
        set
        {
            if (ReferenceEquals(_selectedWorkspaceFile, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedWorkspaceFile, value);

            if (value is not null)
            {
                _ = ShowWorkspaceFileAsync(value.Path);
            }
        }
    }

    private int _retargetInFlight;

    /// <summary>
    /// What a single click in the sidebar does: show the clicked file in the tab
    /// that is on screen. A folder tab previews it inside itself; a log tab is
    /// retargeted at it, so the sidebar stays a navigator while reading. A
    /// double click goes through <see cref="AddTab"/> instead.
    /// </summary>
    public async Task ShowWorkspaceFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (!File.Exists(filePath))
        {
            // The list comes from a background scan, so a row can be dead by
            // the time it is clicked. Swallowing the click looks like a hang.
            StatusMessage = $"File not found: {Path.GetFileName(filePath)}";
            return;
        }

        var fullPath = Path.GetFullPath(filePath);

        if (SelectedTab is { IsFolder: true } folder)
        {
            if (folder.Preview is { } existing &&
                string.Equals(existing.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                _folderPreviewPaths[folder.FilePath] = fullPath;
                WorkspacePreviewChanged?.Invoke();
                UpdateSelectedWorkspaceFile(fullPath);
                return;
            }

            var replaced = folder.Preview;

            var preview = new TabViewModel(fullPath);
            AttachLinesPerSecondCounter(preview);

            // Each preview owns a rate window keyed by its own tab model, so
            // the replaced one has to be released here or every file the user
            // clicks leaks a tab model plus its log buffer.
            if (replaced is not null && _rateSubscriptions.Remove(replaced, out var replacedSubscription))
            {
                replacedSubscription.Dispose();
            }

            folder.SetPreview(preview);
            _folderPreviewPaths[folder.FilePath] = fullPath;
            WorkspacePreviewChanged?.Invoke();
            SyncActiveLogTab();
            LoadWorkspaceFor(folder);

            await StartTailingForAsync(preview);

            // Free the replaced preview's held lines now that its source has
            // stopped, so switching large files in folder mode does not keep
            // every previously previewed file's buffer alive.
            replaced?.ClearEvents();

            UpdateSelectedWorkspaceFile(fullPath);
            return;
        }

        if (SelectedTab is { } logTab)
        {
            await SwitchLogTabToFileAsync(logTab, fullPath);
        }
    }

    /// <summary>
    /// Retargets a log tab at another file: stop the old source, drop the old
    /// file's lines and its rate window, then tail the new file from the top.
    /// </summary>
    private async Task SwitchLogTabToFileAsync(TabViewModel tab, string filePath)
    {
        if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // One click reaches this twice: the sidebar's SelectedItem write-back
        // and the pointer handler. StopCurrentSourceAsync suspends, so without
        // an interlock both calls clear the guard above and start a second
        // source on the same tab.
        if (Interlocked.Exchange(ref _retargetInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            await StopCurrentSourceAsync();

            // Re-check: the other entry point may have completed this retarget
            // while we were suspended.
            if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_rateSubscriptions.Remove(tab, out var rateSubscription))
            {
                rateSubscription.Dispose();
            }

            tab.LoadFile(filePath);
            AttachLinesPerSecondCounter(tab);
            SyncActiveLogTab();

            await StartTailingForAsync(tab);
        }
        finally
        {
            Volatile.Write(ref _retargetInFlight, 0);
        }
    }

    private async Task OpenFileAsync()
    {
        var selectedPath = await ShowOpenFileDialog.Handle(Unit.Default);
        if (string.IsNullOrEmpty(selectedPath))
        {
            return;
        }

        await OpenFileAndAddTabAsync(selectedPath);
    }

    /// <summary>
    /// Open a file by creating (or reusing) a tab and starting its tail source.
    /// Used by the File→Open flow; dropped-file path goes through <see cref="AddTab"/>.
    /// </summary>
    internal async Task OpenFileAndAddTabAsync(string filePath)
    {
        var existing = FindTabByPath(filePath);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new TabViewModel(filePath);
        AttachLinesPerSecondCounter(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        // Tail will start via SelectedTab observer.
        await Task.CompletedTask;
    }

    public void AddTab(string filePath)
    {
        var existingTab = Tabs.FirstOrDefault(t =>
            !t.IsFolder &&
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existingTab != null)
        {
            SelectedTab = existingTab;
            return;
        }

        var tab = new TabViewModel(filePath);
        AttachLinesPerSecondCounter(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        // Tail starts via WhenAnyValue(SelectedTab) subscription above.
    }

    /// <summary>
    /// Opens a folder as a workspace tab. Dropping the same folder again
    /// selects the existing tab instead of adding a duplicate — folder
    /// identity is the full path, compared case-insensitively.
    /// </summary>
    public void OpenFolderTab(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(folderPath);
        var existing = Tabs.FirstOrDefault(t =>
            t.IsFolder && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new TabViewModel(fullPath, isFolder: true);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    private readonly Dictionary<string, string> _folderPreviewPaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Re-opens saved workspace folders. Folders that no longer exist are
    /// skipped silently; the stale entry disappears on the next save.
    /// Does not select anything — <see cref="RestoreTabs"/> picks the active
    /// tab afterwards.
    /// </summary>
    public void RestoreWorkspaces(IReadOnlyList<string> folderPaths) =>
        RestoreWorkspaces(folderPaths, (IReadOnlyList<string?>?)null);

    public void RestoreWorkspaces(IReadOnlyList<string> folderPaths, IReadOnlyList<string?>? previewFiles)
    {
        var index = 0;
        foreach (var folder in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                index++;
                continue;
            }

            var fullPath = Path.GetFullPath(folder);
            if (!Tabs.Any(t => t.IsFolder && string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                Tabs.Add(new TabViewModel(fullPath, isFolder: true));
            }

            if (previewFiles is not null && index < previewFiles.Count &&
                !string.IsNullOrWhiteSpace(previewFiles[index]))
            {
                _folderPreviewPaths[fullPath] = previewFiles[index]!;
            }

            index++;
        }

        // Otherwise a session that had folders but no surviving log file comes
        // up with tabs and nothing selected: blank content, and the empty
        // state is suppressed because the tab count is not zero.
        if (SelectedTab is null)
        {
            SelectedTab = Tabs.FirstOrDefault(t => t.IsFolder) ?? Tabs.LastOrDefault();
        }

        if (SelectedTab is { IsFolder: true } selectedFolder &&
            _folderPreviewPaths.TryGetValue(selectedFolder.FilePath, out var previewPath) &&
            !string.IsNullOrWhiteSpace(previewPath))
        {
            _ = ShowWorkspaceFileAsync(previewPath);
        }
    }

    /// <summary>
    /// Restore a saved session in one batch: add every tab first, then select
    /// the active one once, so the SelectedTab observer starts a single tail
    /// instead of restarting per tab.
    /// </summary>
    public void RestoreTabs(IReadOnlyList<string> paths, string? activePath)
    {
        if (paths.Count == 0)
        {
            return;
        }

        TabViewModel? active = null;
        foreach (var path in paths)
        {
            if (FindTabByPath(path) is not null)
            {
                continue;
            }

            var tab = new TabViewModel(path);
            AttachLinesPerSecondCounter(tab);
            Tabs.Add(tab);

            if (activePath is not null &&
                string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                active = tab;
            }
        }

        SelectedTab = active ?? Tabs.LastOrDefault();
    }

    public void CloseTab(TabViewModel tab)
    {
        var closingActive = SelectedTab == tab;

        // Dispose the rate-counter subscription for this tab so we don't leak
        // when the user closes many tabs over the session.
        if (_rateSubscriptions.Remove(tab, out var rateSub))
        {
            rateSub.Dispose();
        }

        // Keep the per-folder preview bookkeeping in sync so closing a folder
        // tab does not leak its remembered preview path (or its preview's lines).
        if (tab.IsFolder)
        {
            _folderPreviewPaths.Remove(tab.FilePath);
            tab.Preview?.ClearEvents();
        }

        Tabs.Remove(tab);

        if (closingActive)
        {
            // Stop the running source if the closed tab was the active one.
            Task.Run(StopCurrentSourceAsync);
            SelectedTab = Tabs.FirstOrDefault();
        }
    }

    /// <summary>
    /// Tracks a 1-second rolling rate per tab based on added lines (not
    /// CollectionChanged events, so eviction never inflates the rate and
    /// bulk Reset notifications stay correct). Cleanup happens in
    /// <see cref="CloseTab"/>.
    /// </summary>
    private void AttachLinesPerSecondCounter(TabViewModel tab)
    {
        if (!_rateWindows.ContainsKey(tab))
        {
            _rateWindows[tab] = new Queue<DateTimeOffset>();
        }

        _rateSubscriptions[tab] = Disposable.Create(() => _rateWindows.Remove(tab));
    }

    private void RecordLinesForRate(TabViewModel tab, int addedCount)
    {
        if (addedCount <= 0)
        {
            return;
        }

        if (!_rateWindows.TryGetValue(tab, out var window))
        {
            window = new Queue<DateTimeOffset>();
            _rateWindows[tab] = window;
        }

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < addedCount; i++)
        {
            window.Enqueue(now);
        }

        PruneWindow(window, now);
        tab.LinesPerSecond = window.Count;
    }

    private void PruneRates()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (tab, window) in _rateWindows.ToArray())
        {
            var before = window.Count;
            PruneWindow(window, now);
            if (window.Count != before)
            {
                tab.LinesPerSecond = window.Count;
            }
        }
    }

    /// <summary>
    /// Copies the source's true line count onto the tab on screen. Polled on
    /// the one-second tick because the count is computed off-thread once and
    /// then advances with each read chunk; one status-bar refresh per second
    /// is enough and keeps the per-chunk path free of dispatcher work.
    /// </summary>
    private void SyncLineCount()
    {
        var tab = _tailingTab;
        var source = CurrentSource;
        if (tab is null || source is null)
        {
            return;
        }

        var total = source.TotalLineCount;
        if (tab.TotalLineCount != total)
        {
            tab.TotalLineCount = total;
        }
    }

    private static void PruneWindow(Queue<DateTimeOffset> window, DateTimeOffset now)
    {
        while (window.Count > 0 && (now - window.Peek()) > TimeSpan.FromSeconds(1))
        {
            window.Dequeue();
        }
    }

    /// <summary>
    /// Takes one resource reading for the status bar: the app's CPU and memory, plus
    /// the tab on screen's read rate and buffer memory. Driven by the one-second tick
    /// and exposed internally so tests can read a sample without waiting for it.
    /// </summary>
    /// <remarks>
    /// The read rate is the growth of the tailed file's size, which is what the tail
    /// actually ingests. The one-time bulk read of a freshly opened file is not
    /// reported as a spike: the tab already knows the file's full size from its stat
    /// at open, so that lands in the baseline rather than in a rate.
    /// </remarks>
    internal void SampleResourcesNow()
    {
        var usage = _resourceSampler.Sample();

        AppResourceText = $"CPU {usage.CpuPercent:0.#}% · RAM {WorkspaceFile.FormatSize(usage.WorkingSetBytes)}";

        var tab = ActiveLogTab;
        if (tab is null)
        {
            // Nothing is being read, so the last tab's rate must not survive in its
            // tooltip as a stale number.
            ReleaseReadRateBaseline();
            TabResourceText = "Read — · buf —";
            return;
        }

        var readRate = MeasureReadRate(tab);
        tab.ReadBytesPerSecond = readRate;

        var readText = tab.IsTailing
            ? $"{WorkspaceFile.FormatSize((long)readRate)}/s"
            : "—";

        TabResourceText = $"Read {readText} · buf ~{WorkspaceFile.FormatSize(tab.EstimatedBufferBytes)}";
    }

    /// <summary>
    /// Bytes per second the given tab read since the previous sample, measured from
    /// its file size. Establishes a fresh baseline when the tab is not the one being
    /// measured — or has been retargeted at a different file, which re-stats
    /// <c>FileSize</c> to the new file's full length on the same instance — so a
    /// resumed or retargeted tail cannot credit the whole gap to one second. A
    /// rotated or truncated file shrinks, and negative bytes read is meaningless.
    /// </summary>
    private double MeasureReadRate(TabViewModel tab)
    {
        if (!ReferenceEquals(tab, _readRateTab) ||
            !string.Equals(_readRateBaselinePath, tab.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseReadRateBaseline();
            _readRateTab = tab;
            _readRateBaselinePath = tab.FilePath;
            _readRateBaselineSize = tab.FileSize;
            return 0;
        }

        if (!tab.IsTailing)
        {
            _readRateBaselineSize = tab.FileSize;
            return 0;
        }

        var growth = tab.FileSize - _readRateBaselineSize;
        _readRateBaselineSize = tab.FileSize;

        return growth > 0 ? growth : 0;
    }

    private void ReleaseReadRateBaseline()
    {
        if (_readRateTab is { } previous)
        {
            previous.ReadBytesPerSecond = 0;
        }

        _readRateTab = null;
        _readRateBaselinePath = null;
        _readRateBaselineSize = 0;
    }

    public void Dispose()
    {
        // Tear down the one-second tick: the interval subscription holds this
        // instance — and every buffer its tabs hold — alive and keeps sampling
        // with nothing left to display it.
        //
        // The sampler is deliberately left running. It wraps a handle that lives
        // for the life of the process, and closing it here deadlocks against an
        // in-flight sample: disposing both the tick and the sampler hangs the
        // suite four times out of six, disposing the tick alone never does. One
        // handle per process is the cheaper failure.
        _rateTimer.Dispose();
    }

    public TabViewModel? FindTabByPath(string filePath)
    {
        return Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task StopCurrentSourceAsync()
    {
        if (CurrentSource != null)
        {
            if (_onInitialLoadedHandler != null)
            {
                CurrentSource.InitialLogLoaded -= _onInitialLoadedHandler;
                _onInitialLoadedHandler = null;
            }

            await CurrentSource.StopAsync();
            _eventsSubscription?.Dispose();
            _eventsSubscription = null;
            CurrentSource = null;
        }

        // Drop the shared buffer's retained events so switching files does not
        // accumulate every previously tailed file's lines for the process
        // lifetime. Search snapshots tab.LogEvents, not this buffer, so this is
        // safe.
        _buffer.Clear();

        var stopped = _tailingTab;
        _tailingTab = null;
        if (stopped is not null)
        {
            stopped.IsTailing = false;
        }
    }

    private async Task StartTailingAsync(TabViewModel tab, bool resumeOnly = false)
    {
        await StopCurrentSourceAsync();

        // Refresh size & modified time in case the file changed between tab
        // creation and tail start (e.g. dropped file is appended to right
        // after drop). Best-effort; ignore IO errors.
        try
        {
            var info = new FileInfo(tab.FilePath);
            if (info.Exists)
            {
                tab.FileSize = info.Length;
                tab.LastModified = info.LastWriteTime;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        tab.IsTailing = true;
        tab.Status = resumeOnly ? "tailing" : "loading";

        var source = _sourceFactory.CreateFileSource(tab.FilePath, BufferCapacity);
        CurrentSource = source;
        _tailingTab = tab;

        if (resumeOnly)
        {
            // Tab already has its historical snapshot — skip re-reading the
            // whole file. Start the source from the existing end of file so
            // only new appends come in as live events.
            var resumeOffset = new FileInfo(tab.FilePath).Length;
            source.SeekToOffset(resumeOffset);
        }

        var historicalLock = new object();
        var pendingHistoricalEvents = new List<EnrichedLogEvent>();
        var historicalFlushed = false;

        void FlushInitialLogLoaded()
        {
            List<EnrichedLogEvent> snapshot;
            lock (historicalLock)
            {
                snapshot = new List<EnrichedLogEvent>(pendingHistoricalEvents);
                pendingHistoricalEvents.Clear();
                historicalFlushed = true;
            }

            if (snapshot.Count > 0)
            {
                FlushPendingHistorical(tab, snapshot);
            }
            else
            {
                tab.Status = "tailing";
            }
        }

        _onInitialLoadedHandler = () =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                FlushInitialLogLoaded();
            }
            else
            {
                Dispatcher.UIThread.Post(FlushInitialLogLoaded);
            }
        };
        source.InitialLogLoaded += _onInitialLoadedHandler;

        _eventsSubscription = source.Events
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Select(raw => (Raw: raw, Enriched: Enrich.Transform(raw)))
            .Buffer(TimeSpan.FromMilliseconds(50), 2000)
            .Where(batch => batch.Count > 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                onNext: batch =>
                {
                    if (resumeOnly)
                    {
                        OnNewEventsBatch(tab, batch.Select(b => b.Enriched).ToList());
                        return;
                    }

                    var isHistoricalBatch = batch[0].Raw.IsHistorical;
                    var bufferHistoricalBatch = false;
                    lock (historicalLock)
                    {
                        bufferHistoricalBatch = isHistoricalBatch && !historicalFlushed;
                        if (bufferHistoricalBatch)
                        {
                            foreach (var (_, enriched) in batch)
                            {
                                pendingHistoricalEvents.Add(enriched);
                            }
                            // Bound the accumulator to the most recent lines (memory).
                            var backlog = pendingHistoricalEvents.Count - BufferCapacity;
                            if (backlog > 0)
                            {
                                pendingHistoricalEvents.RemoveRange(0, backlog);
                            }
                        }
                    }

                    if (bufferHistoricalBatch)
                    {
                        return;
                    }

                    List<EnrichedLogEvent>? snapshot = null;
                    lock (historicalLock)
                    {
                        if (!historicalFlushed)
                        {
                            var excess = pendingHistoricalEvents.Count - BufferCapacity;
                            if (excess > 0)
                            {
                                pendingHistoricalEvents.RemoveRange(0, excess);
                            }
                            snapshot = new List<EnrichedLogEvent>(pendingHistoricalEvents);
                            pendingHistoricalEvents.Clear();
                        }
                    }

                    if (snapshot is { Count: > 0 })
                    {
                        FlushPendingHistorical(tab, snapshot);
                    }

                    OnNewEventsBatch(tab, batch.Select(b => b.Enriched).ToList());
                },
                onError: ex =>
                {
                    tab.IsTailing = false;
                    tab.Status = "error";
                    StatusMessage = $"Error: {ex.Message}";
                });

        await source.StartAsync(CancellationToken.None);
    }

    private void FlushPendingHistorical(TabViewModel tab, List<EnrichedLogEvent> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        tab.Status = "tailing";

        var capacity = BufferCapacity;
        var rendered = snapshot.Count > capacity
            ? snapshot.GetRange(snapshot.Count - capacity, capacity)
            : snapshot;

        tab.AddLogEvents(rendered);

        var excess = tab.LogEvents.Count - capacity;
        if (excess > 0)
        {
            tab.EvictFromFront(excess);
        }

        foreach (var item in rendered)
        {
            _buffer.Add(item);
        }

        RecordLinesForRate(tab, rendered.Count);

        sw.Stop();
        _logger?.Info($"[InitialLoad:UI] File: '{Path.GetFileName(tab.FilePath)}', Lines Rendered: {rendered.Count:N0}, UI Flush Time: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalSeconds:F2}s)");
    }

    private void OnNewEventsBatch(TabViewModel tab, IList<EnrichedLogEvent> batch)
    {
        if (batch.Count == 0) return;

        // Bulk evict + bulk append: 2 UI notifications per batch instead of
        // ~2*batch.Count. Avoids O(n*m) RemoveAt(0) loop at 50k capacity.
        var excess = (tab.LogEvents.Count + batch.Count) - BufferCapacity;
        if (excess > 0)
        {
            tab.EvictFromFront(excess);
        }

        foreach (var item in batch)
        {
            _buffer.Add(item);
        }

        tab.AddLogEvents(batch);
        RecordLinesForRate(tab, batch.Count);

        try
        {
            var info = new FileInfo(tab.FilePath);
            if (info.Exists)
            {
                tab.FileSize = info.Length;
                tab.LastModified = info.LastWriteTime;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Clear()
    {
        _buffer.Clear();
        if (SelectedTab is not null)
        {
            SelectedTab.ClearEvents();
        }
    }

    private static string BuildTabStatus(TabViewModel tab, int bufferCapacity, string matchCountText)
    {
        var size = tab.FileSize;
        var sizeText = size switch
        {
            >= 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024 * 1024):0.##} GB",
            >= 1024L * 1024 => $"{size / (1024.0 * 1024):0.##} MB",
            >= 1024 => $"{size / 1024.0:0.##} KB",
            _ => $"{size} B",
        };

        // Use em-dash placeholder when modified time is unavailable (file stat
        // failed or missing). Avoids empty string in the status bar which
        // produces a double "•" separator and looks broken.
        var modified = tab.LastModified == default
            ? "—"
            : tab.LastModified.ToString("yyyy-MM-dd HH:mm:ss");

        // Always show rate — including 0 when idle — so the status bar reads
        // consistently (no missing field that flickers in/out as events arrive).
        // Prefer the file's true line count once the source has counted it;
        // until then show what is rendered and say the count is still running.
        var linesCount = tab.TotalLineCount is { } totalLines
            ? WorkspaceFile.FormatLineCount(totalLines)
            : $"{WorkspaceFile.FormatLineCount(tab.LogEvents.Count)} (counting…)";
        var rate = $"{tab.LinesPerSecond:0.#} lines/s";

        var state = tab.IsTailing ? "tailing" : tab.Status;

        // The active query's count rides along in the log area's status line,
        // so the results are visible without the panel open.
        var searchSuffix = string.IsNullOrEmpty(matchCountText) ? string.Empty : $" | {matchCountText}";

        return $"{tab.FilePath} | {sizeText} | {modified} | {linesCount} | {rate} | {state}{searchSuffix}";
    }

}
