using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;

namespace LogTail.UI.ViewModels;

public sealed class SessionTracker : IDisposable
{
    private readonly SessionStore _store;
    private readonly ObservableCollection<TabViewModel> _tabs;
    private readonly List<string> _unresolved = new();
    private readonly CompositeDisposable _subscriptions = new();
    private TabViewModel? _currentSelected;

    public SessionTracker(
        SessionStore store,
        ObservableCollection<TabViewModel> tabs,
        IObservable<TabViewModel?> selectedTab,
        ILogTailLogger? logger = null)
    {
        _store = store;
        _tabs = tabs;

        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _tabs.CollectionChanged += handler,
                handler => _tabs.CollectionChanged -= handler)
            .Subscribe(_ => OnChanged())
            .DisposeWith(_subscriptions);

        selectedTab
            .Subscribe(tab =>
            {
                _currentSelected = tab;
                OnChanged();
            })
            .DisposeWith(_subscriptions);
    }

    public bool IsEnabled { get; set; }

    public void SeedUnresolved(IEnumerable<string> paths)
    {
        _unresolved.Clear();
        _unresolved.AddRange(paths);
    }

    public void SaveNow()
    {
        if (!IsEnabled)
        {
            return;
        }

        // Folder tabs and log tabs are the same collection, so split them here:
        // LogFileValidator rejects directories, so a folder path left in Files
        // would come back as a "missing file" on the next restore.
        var files = new List<string>();
        var folders = new List<string>();
        foreach (var tab in _tabs)
        {
            if (tab.IsFolder)
            {
                AddIfMissing(folders, tab.FilePath);
            }
            else
            {
                AddIfMissing(files, tab.FilePath);
            }
        }

        foreach (var path in _unresolved)
        {
            AddIfMissing(files, path);
        }

        var selected = _currentSelected;
        var activeFile = selected is { IsFolder: false } ? selected.FilePath : null;
        var activeFolder = selected is { IsFolder: true } ? selected.FilePath : null;

        _store.Save(new SessionState(files, activeFile, folders, activeFolder));
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
    }

    private void OnChanged()
    {
        SaveNow();
    }

    private static void AddIfMissing(List<string> paths, string candidate)
    {
        if (!paths.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            paths.Add(candidate);
        }
    }
}
