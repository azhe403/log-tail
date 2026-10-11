using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using LogTail.Core.Models;
using LogTail.UI.Collections;
using ReactiveUI;

namespace LogTail.UI.ViewModels;

/// <summary>
/// Search state for the main window: the query, the scope, and the match list
/// shown in the bottom results panel. Holds no reference back to
/// MainWindowViewModel — the parent pushes the selected tab and an activate
/// callback — so the class is testable without Avalonia views.
/// </summary>
public sealed class SearchViewModel : ReactiveObject
{
    private const double DefaultPanelHeight = 180;
    private const double MinPanelHeight = 80;
    private const double MaxPanelHeight = 800;
    private const int MaxMatches = 10_000;

    private static readonly GridLength ZeroHeight = new(0, GridUnitType.Pixel);

    private readonly ObservableCollection<TabViewModel> _tabs;
    private readonly Action<TabViewModel> _activateTab;
    private readonly Subject<Unit> _refresh = new();
    private readonly CompositeDisposable _subscriptions = new();

    private string _searchText = string.Empty;
    private bool _searchAllTabs;
    private bool _isPanelOpen;
    private TabViewModel? _selectedTab;
    private int _matchCount;
    private bool _capped;
    private GridLength _panelHeight = new(DefaultPanelHeight, GridUnitType.Pixel);
    private LogSearchMatch? _selectedMatch;
    private int _searchGeneration;

    public SearchViewModel(ObservableCollection<TabViewModel> tabs, Action<TabViewModel> activateTab)
    {
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        _activateTab = activateTab ?? throw new ArgumentNullException(nameof(activateTab));

        Matches = new BulkObservableCollection<LogSearchMatch>();
        JumpToMatchCommand = ReactiveCommand.Create<LogSearchMatch>(JumpToMatch);
        CloseCommand = ReactiveCommand.Create(ClosePanel);
        ScrollToLine = new Interaction<LogSearchMatch, bool>();

        // Tab open/close can change results even when the query did not.
        _tabs.CollectionChanged += (_, _) => _refresh.OnNext(Unit.Default);

        Observable.Merge(
                this.WhenAnyValue(x => x.SearchText, x => x.SearchAllTabs).Select(_ => Unit.Default),
                _refresh)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RunSearch())
            .DisposeWith(_subscriptions);
    }

    public BulkObservableCollection<LogSearchMatch> Matches { get; }

    public ReactiveCommand<LogSearchMatch, Unit> JumpToMatchCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>
    /// The view's scroll hook: the view scrolls the on-screen log to the
    /// matched line and sets the output to whether the line was found.
    /// </summary>
    public Interaction<LogSearchMatch, bool> ScrollToLine { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);

            // Typing a query (re)opens the panel, so a closed panel never
            // hides freshest results from the user.
            if (!string.IsNullOrWhiteSpace(value))
            {
                IsPanelOpen = true;
            }

            this.RaisePropertyChanged(nameof(HasQuery));
            this.RaisePropertyChanged(nameof(MatchCountText));
            this.RaisePropertyChanged(nameof(ShowPanel));
            this.RaisePropertyChanged(nameof(PanelHeight));
        }
    }

    /// <summary>
    /// Off (default): search only the log on screen. On: every open tab.
    /// </summary>
    public bool SearchAllTabs
    {
        get => _searchAllTabs;
        set => this.RaiseAndSetIfChanged(ref _searchAllTabs, value);
    }

    public TabViewModel? SelectedTab => _selectedTab;

    /// <summary>
    /// The tab pushed by the parent whenever the selection changes, including
    /// a folder tab swapping its preview (same tab reference, new log source).
    /// </summary>
    public void SetActiveTab(TabViewModel? tab)
    {
        _selectedTab = tab;
        _refresh.OnNext(Unit.Default);
    }

    public int MatchCount
    {
        get => _matchCount;
        private set => this.RaiseAndSetIfChanged(ref _matchCount, value);
    }

    public bool HasQuery => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Whether the results panel is on screen. Decoupled from the query:
    /// closing the panel keeps the text, and Ctrl+F or the next keystroke
    /// opens it again.
    /// </summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPanelOpen, value);

            // Computed dependents: the view binds ShowPanel (visibility) and
            // PanelHeight (row height), neither of which RaiseAndSetIfChanged
            // announces by itself.
            this.RaisePropertyChanged(nameof(ShowPanel));
            this.RaisePropertyChanged(nameof(PanelHeight));
        }
    }

    /// <summary>
    /// False when there is no query or the panel is closed — what the view
    /// binds visibility (and row height) to.
    /// </summary>
    public bool ShowPanel => HasQuery && IsPanelOpen;

    public void OpenPanel() => IsPanelOpen = true;

    public void ClosePanel() => IsPanelOpen = false;

    public string MatchCountText => !HasQuery
        ? string.Empty
        : MatchCount == 0
            ? "No matches"
            : _capped
                // Capped: show the display cap with "+" ("at least this many")
                // — the list only holds the first MaxMatches items, so the exact
                // total is not something the user can act on.
                ? $"{MaxMatches:N0}+ results"
                : $"{MatchCount:N0} results";

    public LogSearchMatch? SelectedMatch
    {
        get => _selectedMatch;
        set => this.RaiseAndSetIfChanged(ref _selectedMatch, value);
    }

    /// <summary>
    /// The results row height. Collapses to zero while the panel is hidden —
    /// no query, or closed — so the panel and its splitter reserve no space;
    /// the last dragged height is kept in between. GridLength (not double) so
    /// binding to RowDefinition.Height needs no converter.
    /// </summary>
    public GridLength PanelHeight
    {
        get => ShowPanel ? _panelHeight : ZeroHeight;
        set
        {
            // The splitter writes the dragged height back; the zero the
            // binding echoes while the panel is collapsed is ignored, and the
            // value is clamped so the panel can never swallow the log area.
            if (value.IsAbsolute && value.Value > 0)
            {
                var clamped = Math.Clamp(value.Value, MinPanelHeight, MaxPanelHeight);
                this.RaiseAndSetIfChanged(ref _panelHeight, new GridLength(clamped, GridUnitType.Pixel));
            }
        }
    }

    /// <summary>
    /// Activates the match's tab and asks the view to scroll to the line. A
    /// line evicted from the ring buffer (or a tab closed since the search
    /// ran) is ignored — checked before switching tabs.
    /// </summary>
    public void JumpToMatch(LogSearchMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (!match.Tab.LogSource.LogEvents.Contains(match.Event))
        {
            return;
        }

        _activateTab(match.Tab);
        ScrollToLine.Handle(match).Subscribe(_ => { });
    }

    private void RunSearch()
    {
        var query = SearchText.Trim();

        if (query.Length == 0)
        {
            Interlocked.Increment(ref _searchGeneration);
            Matches.Clear();
            MatchCount = 0;
            _capped = false;
            this.RaisePropertyChanged(nameof(MatchCountText));
            return;
        }

        // Snapshot on the UI thread: tailing appends mutate LogEvents from
        // the dispatcher, so enumerating them from the scan thread races.
        var snapshots = SearchAllTabs
            ? Snapshot(_tabs)
            : _selectedTab is { } selected
                ? Snapshot(new[] { selected })
                : Array.Empty<(TabViewModel Tab, EnrichedLogEvent[] Events)>();

        var generation = Interlocked.Increment(ref _searchGeneration);

        Observable.Start(() => Scan(query, snapshots), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(result =>
            {
                if (generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                Matches.Clear();
                Matches.AddRange(result.Matches);
                MatchCount = result.Total;
                _capped = result.Total > MaxMatches;
                this.RaisePropertyChanged(nameof(MatchCountText));
            });
    }

    private static (TabViewModel Tab, EnrichedLogEvent[] Events)[] Snapshot(
        IEnumerable<TabViewModel> tabs)
    {
        var list = new List<(TabViewModel, EnrichedLogEvent[])>();
        foreach (var tab in tabs)
        {
            list.Add((tab, tab.LogSource.LogEvents.ToArray()));
        }
        return list.ToArray();
    }

    private static (List<LogSearchMatch> Matches, int Total) Scan(
        string query,
        (TabViewModel Tab, EnrichedLogEvent[] Events)[] snapshots)
    {
        var matches = new List<LogSearchMatch>();
        var total = 0;

        foreach (var (tab, events) in snapshots)
        {
            foreach (var logEvent in events)
            {
                if (!logEvent.Raw.Line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total++;
                if (matches.Count < MaxMatches)
                {
                    matches.Add(new LogSearchMatch(tab, logEvent));
                }
            }
        }

        return (matches, total);
    }
}
