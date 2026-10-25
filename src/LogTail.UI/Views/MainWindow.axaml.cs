using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.IO;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using LogTail.Core.Workspace;
using LogTail.UI.ViewModels;
using ReactiveUI;

namespace LogTail.UI.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private readonly SettingsStore? _settingsStore;
    private readonly ILogTailLogger? _logger;
    private readonly Subject<WindowPlacement> _savePlacementSubject = new();
    private PixelPoint? _lastNormalPosition;
    private double? _lastNormalWidth;
    private double? _lastNormalHeight;
    private bool _isRestoringPlacement;

    private ScrollViewer? _scrollViewer;
    private TextBox? _searchTextBox;
    private bool _isProgrammaticScroll;
    private double? _lockedOffsetY;
    private bool _scrollRequested;
    private IDisposable? _activeTabListener;
    private bool _suppressOffsetEvents;

    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(SettingsStore? settingsStore, ILogTailLogger? logger = null)
    {
        _settingsStore = settingsStore;
        _logger = logger;

        InitializeComponent();

        _isRestoringPlacement = true;
        try
        {
            RestoreWindowPlacement();
        }
        finally
        {
            _isRestoringPlacement = false;
        }

        _savePlacementSubject
            .Throttle(TimeSpan.FromMilliseconds(200))
            .Subscribe(placement =>
            {
                try
                {
                    _settingsStore?.Update(s => s with { WindowPlacement = placement });
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Failed to save window placement: {ex.Message}");
                }
            });

        PositionChanged += (_, _) => NotifyPositionChanged(Position);

        SizeChanged += (_, e) =>
        {
            if (!_isRestoringPlacement && WindowState == WindowState.Normal)
            {
                var w = e.NewSize.Width >= 100 ? e.NewSize.Width : GetActualWidth();
                var h = e.NewSize.Height >= 100 ? e.NewSize.Height : GetActualHeight();
                _lastNormalWidth = w;
                _lastNormalHeight = h;
                QueueSavePlacement();
            }
        };

        PropertyChanged += (_, e) =>
        {
            if (!_isRestoringPlacement && e.Property == WindowStateProperty)
            {
                QueueSavePlacement();
            }
        };

        Closing += (_, _) =>
        {
            _savePlacementSubject.Dispose();
            SaveWindowPlacement();
        };

        // DragDrop handlers for dropping files from OS file manager
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);

        // handledEventsToo: the search TextBox marks Enter handled itself, but
        // the find shortcuts must still reach the window.
        _searchTextBox = this.FindControl<TextBox>("SearchTextBox");

        // Returning to the find box reopens the results panel: the panel is a
        // view of the query, so focusing the box brings the results back.
        // Focusing is also the reliable reopen path for Ctrl+F, whose bubbled
        // KeyDown was observed not to reach this window handler.
        if (_searchTextBox is not null)
        {
            _searchTextBox.GotFocus += (_, _) => ViewModel?.Search.OpenPanel();
        }

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        this.WhenActivated(disposables =>
        {
            // Register ReactiveUI Interaction handler for File Picker dialog
            ViewModel?.ShowOpenFileDialog.RegisterHandler(DoShowOpenFileDialogAsync)
                .DisposeWith(disposables);

            ViewModel?.ShowSettingsDialog.RegisterHandler(DoShowSettingsAsync)
                .DisposeWith(disposables);

            // Search: the view scrolls the on-screen log to the matched line.
            ViewModel?.Search.ScrollToLine.RegisterHandler(async interaction =>
            {
                // A jump to another tab needs that tab's content host to exist
                // and lay out first, so the scroll waits one layout pass.
                var scrolled = await Dispatcher.UIThread.InvokeAsync(
                    () => ScrollToMatch(interaction.Input),
                    DispatcherPriority.Loaded);
                interaction.SetOutput(scrolled);
            })
            .DisposeWith(disposables);

            // Subscribe to ViewModel AutoScroll changes using WhenAnyValue
            this.WhenAnyValue(x => x.ViewModel!.AutoScroll)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(autoScroll =>
                {
                    if (!autoScroll && _scrollViewer != null)
                    {
                        _lockedOffsetY = _scrollViewer.Offset.Y;
                    }
                    else if (autoScroll)
                    {
                        _lockedOffsetY = null;
                        RequestScrollToBottom();
                    }
                })
                .DisposeWith(disposables);

            // Subscribe to collection changes for auto-scroll on new log items.
            // Tracked on ActiveLogTab, not SelectedTab: previewing another file
            // inside the same folder tab swaps the log being displayed without
            // the tab itself changing.
            if (ViewModel != null)
            {
                this.WhenAnyValue(x => x.ViewModel!.ActiveLogTab)
                    .DistinctUntilChanged()
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => AttachSelectedTabScrollListener(disposables))
                    .DisposeWith(disposables);
            }

            AttachUserSelectionForwarding(disposables);
        });
    }

    /// <summary>
    /// Keeps the ViewModel as the single owner of which tab is selected.
    ///
    /// SelectedItem is OneWay, because Avalonia's Selector writes its selection
    /// back on a TwoWay binding. While ItemsSource materializes it passes through
    /// index 0 and then its own default, and every one of those transient states
    /// reaches the ViewModel as an apparent tab switch — restarting the tail and
    /// discarding the source that just read the file, and persisting the wrong
    /// ActiveFile into session.json.
    ///
    /// Two things replace that write-back. The bound value is re-asserted once the
    /// control has laid out, because SelectedItem is applied before the restored
    /// tabs materialize and is silently dropped at that point. And genuine user
    /// clicks are forwarded back, deferred one dispatcher turn so startup churn
    /// has settled and only a selection the user actually lands on is propagated.
    /// </summary>
    private void AttachUserSelectionForwarding(CompositeDisposable disposables)
    {
        if (this.FindControl<TabControl>("MainTabControl") is not { } tabControl || ViewModel is not { } vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => tabControl.SelectedItem = vm.SelectedTab,
            DispatcherPriority.Loaded);

        // Local function so the delegate type follows SelectionChanged's own
        // signature instead of naming it.
        void ForwardSelection(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(vm.SelectedTab, tabControl.SelectedItem))
            {
                vm.SelectedTab = tabControl.SelectedItem as TabViewModel;
            }
        });
        tabControl.SelectionChanged += ForwardSelection;
        Disposable.Create(() => tabControl.SelectionChanged -= ForwardSelection).DisposeWith(disposables);
    }

    private void AttachSelectedTabScrollListener(CompositeDisposable disposables)
    {
        _activeTabListener?.Dispose();
        _activeTabListener = null;
        _scrollViewer = null;
        // The lock belongs to the viewer that is being detached.
        _lockedOffsetY = null;

        if (ViewModel?.ActiveLogTab == null)
        {
            return;
        }

        // Switching tabs is not a user scroll action — freeze offset evaluation
        // until the new viewer finishes layout so AutoScroll does not turn off.
        _suppressOffsetEvents = true;

        var tab = ViewModel.ActiveLogTab;
        var tabDisposables = new CompositeDisposable();
        _activeTabListener = tabDisposables;
        Disposable.Create(() => tabDisposables.Dispose()).DisposeWith(disposables);

        // Auto-scroll to bottom on new log events for this tab.
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => tab.LogEvents.CollectionChanged += h,
                h => tab.LogEvents.CollectionChanged -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (ViewModel is { AutoScroll: true } && tab.LogEvents.Count > 0)
                {
                    RequestScrollToBottom();
                }
            })
            .DisposeWith(tabDisposables);

        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = this.FindControl<TabControl>("MainTabControl")?
                .FindDescendantOfType<ScrollViewer>();
            if (scrollViewer == null)
            {
                _suppressOffsetEvents = false;
                return;
            }

            _scrollViewer = scrollViewer;
            scrollViewer.ScrollChanged += OnScrollChanged;
            Disposable.Create(() => scrollViewer.ScrollChanged -= OnScrollChanged)
                .DisposeWith(tabDisposables);

            Dispatcher.UIThread.Post(() =>
            {
                _suppressOffsetEvents = false;
                if (ViewModel is { AutoScroll: true })
                {
                    RequestScrollToBottom();
                }
                else
                {
                    // AutoScroll may already be off when the tab is attached:
                    // hold the position this viewer starts at.
                    _lockedOffsetY = scrollViewer.Offset.Y;
                }
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Background);
    }

    private void RequestScrollToBottom()
    {
        if (ViewModel == null || !ViewModel.AutoScroll || _scrollRequested)
        {
            return;
        }

        _scrollRequested = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollRequested = false;
            if (ViewModel is { AutoScroll: true })
            {
                ScrollToBottom();
            }
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        if (ViewModel?.ActiveLogTab is not { } active || active.LogEvents.Count == 0) return;

        var scrollViewer = _scrollViewer ?? this.FindControl<TabControl>("MainTabControl")?
            .FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            return;
        }

        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        SetScrollOffset(scrollViewer, maxOffsetY);
    }

    /// <summary>
    /// Runs an offset change that must not be mistaken for a user scroll. The
    /// guard is released after layout has settled, so the resulting
    /// ScrollChanged event cannot flip AutoScroll.
    /// </summary>
    private void RunProgrammaticScroll(Action scroll)
    {
        _isProgrammaticScroll = true;
        try
        {
            scroll();
        }
        finally
        {
            Dispatcher.UIThread.Post(() => _isProgrammaticScroll = false, DispatcherPriority.ContextIdle);
        }
    }

    private void SetScrollOffset(ScrollViewer sv, double y)
        => RunProgrammaticScroll(() => sv.Offset = new Vector(sv.Offset.X, y));

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            _searchTextBox?.Focus();
            vm.Search.OpenPanel();
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.C)
        {
            // Copy from the focused log row. A TextBox (the search box) keeps
            // its native copy behaviour.
            var row = this.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .FirstOrDefault(candidate => candidate.IsFocused);
            if (row is null)
            {
                return;
            }

            var text = row.SelectedText;
            if (string.IsNullOrEmpty(text) && row.DataContext is EnrichedLogEvent logEvent)
            {
                text = logEvent.Raw.Line;
            }

            if (!string.IsNullOrEmpty(text))
            {
                _ = CopyTextAsync(text);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape && (_searchTextBox?.IsFocused == true || vm.Search.ShowPanel))
        {
            // Close only: the query stays so Ctrl+F or the next keystroke
            // brings the same results back.
            vm.Search.ClosePanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && vm.Search.ShowPanel &&
                 vm.Search.Matches.FirstOrDefault() is { } first)
        {
            vm.Search.JumpToMatch(first);
            e.Handled = true;
        }
    }

    private void OnSearchResultActivated(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LogSearchMatch match })
        {
            ViewModel?.Search.JumpToMatch(match);
        }
    }

    /// <summary>
    /// Stores the dragged panel height on the view model so the panel keeps
    /// its size when it closes and reopens during the session. The height is
    /// clamped by the view model; any adjustment is written back to the row.
    /// </summary>
    private void OnSearchPanelSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (sender is not GridSplitter { Parent: Grid { RowDefinitions.Count: >= 3 } grid })
        {
            return;
        }

        if (ViewModel is not { } vm)
        {
            return;
        }

        var row = grid.RowDefinitions[2];
        vm.Search.PanelHeight = row.Height;
        if (row.Height != vm.Search.PanelHeight)
        {
            row.Height = vm.Search.PanelHeight;
        }
    }

    /// <summary>
    /// Scrolls the on-screen log so the matched line is visible. BringIntoView
    /// handles a realized container; otherwise the uniform row height (every
    /// log row is 20px, TextWrapping=NoWrap) gives the offset directly.
    /// </summary>
    private bool ScrollToMatch(LogSearchMatch match)
    {
        var events = match.Tab.LogSource.LogEvents;
        var index = events.IndexOf(match.Event);
        if (index < 0 || ViewModel is null)
        {
            return false;
        }

        var scrollViewer = this.FindControl<TabControl>("MainTabControl")?
            .FindDescendantOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            return false;
        }

        if (scrollViewer.FindDescendantOfType<ItemsControl>() is { } itemsControl &&
            itemsControl.ContainerFromIndex(index) is { } container)
        {
            RunProgrammaticScroll(container.BringIntoView);
            return true;
        }

        var rowHeight = scrollViewer.Extent.Height / Math.Max(1, events.Count);
        var target = (index * rowHeight) - (scrollViewer.Viewport.Height / 2);
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

        SetScrollOffset(scrollViewer, Math.Clamp(target, 0, maxOffset));
        return true;
    }

    private async Task DoShowOpenFileDialogAsync(IInteractionContext<Unit, string?> interaction)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select log file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Log files") { Patterns = new[] { "*.log", "*.txt", "*.*" } }
            }
        });

        var selectedPath = result.Count > 0 ? result[0].Path.LocalPath : null;
        interaction.SetOutput(selectedPath);
    }

    private async Task DoShowSettingsAsync(IInteractionContext<Unit, Unit> interaction)
    {
        if (ViewModel is null)
        {
            interaction.SetOutput(Unit.Default);
            return;
        }

        var window = new SettingsWindow
        {
            DataContext = ViewModel.Settings
        };

        await window.ShowDialog(this);
        interaction.SetOutput(Unit.Default);
    }

    private async void OnCopySelectedClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        var text = GetRowText(item)?.SelectedText;
        if (string.IsNullOrEmpty(text))
        {
            text = GetRowEvent(item)?.Raw.Line;
        }

        await CopyTextAsync(text);
    }

    private async void OnCopyLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        var text = GetRowEvent(item)?.Raw.Line ?? GetRowText(item)?.Text;
        await CopyTextAsync(text);
    }

    private static EnrichedLogEvent? GetRowEvent(MenuItem item)
        => (item.Parent as ContextMenu)?.PlacementTarget is Control target
            ? target.DataContext as EnrichedLogEvent
            : null;

    private static SelectableTextBlock? GetRowText(MenuItem item)
        => (item.Parent as ContextMenu)?.PlacementTarget is Control target
            ? target.FindDescendantOfType<SelectableTextBlock>()
            : null;

    private async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || ViewModel == null) return;
        if (_suppressOffsetEvents || _isProgrammaticScroll) return;
        if (_scrollViewer != null && !ReferenceEquals(sv, _scrollViewer)) return;

        var maxOffsetY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        // A scroll can arrive in the same layout pass as new content, so test
        // the offset first. Treating such a scroll as growth would re-apply the
        // lock and snap the view back under the user.
        if (e.OffsetDelta.Y != 0)
        {
            _lockedOffsetY = sv.Offset.Y;

            var atBottom = sv.Offset.Y >= maxOffsetY - 15.0;
            if (ViewModel.AutoScroll)
            {
                // Scrolling up while following pauses the tail.
                if (!atBottom)
                {
                    ViewModel.AutoScroll = false;
                }
            }
            else if (maxOffsetY > 0 && e.OffsetDelta.Y > 0 && atBottom)
            {
                // Reaching the bottom again resumes following, so the user does
                // not have to flip the toggle after scrolling back down.
                ViewModel.AutoScroll = true;
            }
            return;
        }

        // Content grew. While following the tail the collection listener has
        // already asked for the bottom; while paused, hold the offset the user
        // left the view at.
        if (e.ExtentDelta != Vector.Zero &&
            !ViewModel.AutoScroll &&
            _lockedOffsetY is { } lockedY)
        {
            var targetY = Math.Min(lockedY, maxOffsetY);
            if (Math.Abs(sv.Offset.Y - targetY) > 0.5)
            {
                SetScrollOffset(sv, targetY);
            }

            // Front eviction can shrink the extent past the lock; keep the lock
            // inside the new range so the next line cannot jump.
            _lockedOffsetY = targetY;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer is { } dt && dt.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            Classes.Add("drag-over");
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            Classes.Remove("drag-over");
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        Classes.Remove("drag-over");
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Classes.Remove("drag-over");

        var dt = e.DataTransfer;
        if (ViewModel == null || dt == null)
        {
            return;
        }

        var files = dt.TryGetFiles();
        if (files == null)
        {
            return;
        }

        var vm = ViewModel;
        if (vm == null)
        {
            return;
        }

        foreach (var item in files)
        {
            // Avalonia 11: Path is Uri, LocalPath may be encoded or not rooted on some platforms.
            var raw = item.Path.ToString();
            string path;
            try { path = item.Path.LocalPath; } catch { path = raw; }
            try { path = Uri.UnescapeDataString(path); } catch { }
            if (!Path.IsPathRooted(path))
                try { path = new Uri(raw).LocalPath; } catch { }

            if (item is IStorageFolder || Directory.Exists(path))
            {
                vm.OpenFolderTab(path);
                continue;
            }

            if (item is not IStorageFile) continue;
            if (!File.Exists(path)) continue;

            if (LogFileValidator.TryValidateFile(path, out var error))
                vm.AddTab(path);
            else if (error != null)
                vm.StatusMessage = error;
        }
    }

    /// <summary>
    /// Single click in the sidebar: show the log in the tab that is on screen.
    /// The selection is also bound to <c>SelectedWorkspaceFile</c>, which does
    /// the same thing; this handler covers the case where the pointer event
    /// arrives without the selection changing. Both are idempotent for the same
    /// file, and the view model interlocks a log-tab retarget so the pair cannot
    /// start two tail sources.
    ///
    /// PointerPressed rather than PointerReleased, because a release fires on
    /// both clicks of a double-click and the second one has to reach
    /// <see cref="OnWorkspaceFileActivated"/> alone.
    /// </summary>
    private async void OnWorkspaceFileClicked(object? sender, PointerPressedEventArgs e)
    {
        // The second click of a double-click belongs to OnWorkspaceFileActivated.
        // Retargeting here first would make FindTabByPath find the tab this very
        // click moved, so no new tab would open and the previous file would be
        // dropped from the strip.
        if (e.ClickCount > 1)
        {
            return;
        }

        if (sender is not ListBox list ||
            list.SelectedItem is not WorkspaceFile file ||
            string.IsNullOrEmpty(file.Path) ||
            ViewModel == null)
        {
            return;
        }

        await ViewModel.ShowWorkspaceFileAsync(file.Path);
    }

    private async void OnWorkspaceFileActivated(object? sender, TappedEventArgs e)
    {
        // The overflow node carries no path, and folders are not log files —
        // only listed files open a tab.
        if (sender is not ListBox list ||
            list.SelectedItem is not WorkspaceFile file ||
            string.IsNullOrEmpty(file.Path) ||
            ViewModel == null)
        {
            return;
        }

        await ViewModel.OpenFileAndAddTabAsync(file.Path);
    }

    private async void OnTabLogPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox ||
            e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed is false)
        {
            return;
        }

        // Build text from all currently-selected log lines.
        var lines = (listBox.SelectedItems ?? Array.Empty<object?>())
            .OfType<EnrichedLogEvent>()
            .Select(ev => ev.Raw.Line)
            .ToList();

        if (lines.Count == 0)
        {
            return;
        }

        var payload = string.Join(Environment.NewLine, lines);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(payload));

        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void RestoreWindowPlacement()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            var settings = _settingsStore.Load();
            var placement = settings.WindowPlacement;
            if (placement is null)
            {
                return;
            }

            if (placement.Width.HasValue && placement.Width.Value >= 100)
            {
                Width = placement.Width.Value;
                _lastNormalWidth = placement.Width.Value;
            }

            if (placement.Height.HasValue && placement.Height.Value >= 100)
            {
                Height = placement.Height.Value;
                _lastNormalHeight = placement.Height.Value;
            }

            if (placement.X.HasValue && placement.Y.HasValue)
            {
                var targetPoint = new PixelPoint(placement.X.Value, placement.Y.Value);
                _lastNormalPosition = targetPoint;

                var isVisibleOnScreen = true;
                if (Screens is { All.Count: > 0 })
                {
                    isVisibleOnScreen = Screens.ScreenFromPoint(targetPoint) != null;
                }

                if (isVisibleOnScreen)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = targetPoint;
                }
            }

            if (placement.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to restore window placement: {ex.Message}");
        }
    }

    internal void NotifyPositionChanged(PixelPoint pos)
    {
        if (!_isRestoringPlacement && WindowState == WindowState.Normal)
        {
            _lastNormalPosition = pos;
            QueueSavePlacement();
        }
    }

    private void QueueSavePlacement()
    {
        if (_isRestoringPlacement || _settingsStore is null)
        {
            return;
        }

        try
        {
            var isMaximized = WindowState == WindowState.Maximized;
            var pos = _lastNormalPosition ?? Position;
            var currentWidth = GetActualWidth();
            var currentHeight = GetActualHeight();
            var width = isMaximized ? _lastNormalWidth ?? currentWidth : currentWidth;
            var height = isMaximized ? _lastNormalHeight ?? currentHeight : currentHeight;

            var placement = new WindowPlacement(
                X: pos.X,
                Y: pos.Y,
                Width: width,
                Height: height,
                IsMaximized: isMaximized);

            _savePlacementSubject.OnNext(placement);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to capture window placement: {ex.Message}");
        }
    }

    private double GetActualWidth()
    {
        if (!double.IsNaN(Width) && Width >= 100) return Width;
        if (ClientSize.Width >= 100) return ClientSize.Width;
        if (Bounds.Width >= 100) return Bounds.Width;
        return 900;
    }

    private double GetActualHeight()
    {
        if (!double.IsNaN(Height) && Height >= 100) return Height;
        if (ClientSize.Height >= 100) return ClientSize.Height;
        if (Bounds.Height >= 100) return Bounds.Height;
        return 600;
    }

    private void SaveWindowPlacement()
    {
        if (_settingsStore is null)
        {
            return;
        }

        try
        {
            var isMaximized = WindowState == WindowState.Maximized;
            var pos = _lastNormalPosition ?? Position;
            var currentWidth = GetActualWidth();
            var currentHeight = GetActualHeight();
            var width = isMaximized ? _lastNormalWidth ?? currentWidth : currentWidth;
            var height = isMaximized ? _lastNormalHeight ?? currentHeight : currentHeight;

            var placement = new WindowPlacement(
                X: pos.X,
                Y: pos.Y,
                Width: width,
                Height: height,
                IsMaximized: isMaximized);

            _settingsStore.Update(s => s with { WindowPlacement = placement });
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to save window placement: {ex.Message}");
        }
    }
}
