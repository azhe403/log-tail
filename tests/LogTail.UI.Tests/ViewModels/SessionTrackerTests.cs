using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive.Subjects;
using FluentAssertions;
using LogTail.Core.Persistence;
using LogTail.UI.ViewModels;
using Xunit;

namespace LogTail.UI.Tests.ViewModels;

public sealed class SessionTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _sessionStore;
    private readonly ObservableCollection<TabViewModel> _tabs = new();
    private readonly Subject<TabViewModel?> _selected = new();

    public SessionTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-tracker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sessionStore = new SessionStore(_tempDir);
    }

    public void Dispose()
    {
        _selected.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Change_WhenEnabled_PersistsOpenTabsAndActive()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = true };
        var tab = new TabViewModel("a.log");
        _tabs.Add(tab);
        _selected.OnNext(tab);

        var state = _sessionStore.Load();

        state.Files.Should().Contain("a.log");
        state.ActiveFile.Should().Be("a.log");
    }

    [Fact]
    public void Change_WhenDisabled_DoesNotWrite()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = false };

        _tabs.Add(new TabViewModel("a.log"));

        File.Exists(Path.Combine(_tempDir, "session.json")).Should().BeFalse();
    }

    [Fact]
    public void SeedUnresolved_WhenEnabled_KeepsMissingPathsAlongsideTabs()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = true };
        sut.SeedUnresolved(new[] { "missing-old.log" });

        _tabs.Add(new TabViewModel("open.log"));

        var state = _sessionStore.Load();

        state.Files.Should().Contain("open.log");
        state.Files.Should().Contain("missing-old.log");
    }

    [Fact]
    public void SelectedTabChange_UpdatesActiveFile()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = true };
        var a = new TabViewModel("a.log");
        var b = new TabViewModel("b.log");
        _tabs.Add(a);
        _tabs.Add(b);

        _selected.OnNext(a);
        _selected.OnNext(b);

        _sessionStore.Load().ActiveFile.Should().Be("b.log");
    }

    [Fact]
    public void Change_WhenFolderTabOpen_PersistsFolderApartFromLogFiles()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = true };
        var folderTab = new TabViewModel(@"C:\logs\app", isFolder: true);
        _tabs.Add(folderTab);
        _tabs.Add(new TabViewModel("a.log"));

        _selected.OnNext(folderTab);

        var state = _sessionStore.Load();
        state.Files.Should().Equal("a.log");
        state.WorkspaceFolders.Should().Equal(@"C:\logs\app");
        state.ActiveFolder.Should().Be(@"C:\logs\app");
        state.ActiveFile.Should().BeNull();
    }

    [Fact]
    public void Change_WhenFolderTabClosed_RemovesItFromSavedWorkspaces()
    {
        using var sut = new SessionTracker(_sessionStore, _tabs, _selected) { IsEnabled = true };
        var folderTab = new TabViewModel(@"C:\logs\app", isFolder: true);
        _tabs.Add(folderTab);
        _selected.OnNext(folderTab);
        _sessionStore.Load().WorkspaceFolders.Should().Equal(@"C:\logs\app");

        _tabs.Remove(folderTab);
        _selected.OnNext(null);

        var state = _sessionStore.Load();
        state.WorkspaceFolders.Should().BeEmpty();
        state.ActiveFolder.Should().BeNull();
    }
}
