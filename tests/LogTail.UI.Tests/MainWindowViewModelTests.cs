using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using LogTail.Core.Models;
using LogTail.Core.Sources;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LogTail.Core.Workspace;
using LogTail.UI.ViewModels;
using LogTail.UI.Views;
using Xunit;

namespace LogTail.UI.Tests;

[Collection("MainWindowViewModelTests")]
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Constructor_WhenInitialized_HasDefaultProperties()
    {
        var sut = CreateViewModel();

        sut.WindowTitle.Should().Be("Log Tail");
        sut.StatusMessage.Should().Be("No file open");
        sut.CurrentFilePath.Should().BeNull();
        sut.Tabs.Should().BeEmpty();
        sut.SelectedTab.Should().BeNull();
    }

    [AvaloniaFact]
    public void RestoreTabs_WhenCalled_AddsTabsInOrderAndSelectsActive()
    {
        var sut = CreateViewModel();
        var a = CreateLogFile("session-a.log");
        var b = CreateLogFile("session-b.log");
        var c = CreateLogFile("session-c.log");

        sut.RestoreTabs(new[] { a, b, c }, b);

        sut.Tabs.Select(t => t.FilePath).Should().ContainInOrder(a, b, c);
        sut.SelectedTab!.FilePath.Should().Be(b);
    }

    [AvaloniaFact]
    public async Task RestoreTabs_AtStartup_ActuallyLoadsLogLines()
    {
        // Mirrors App.OnFrameworkInitializationCompleted exactly: the ViewModel is
        // constructed first (SelectedTab == null, so the SelectedTab observer's
        // Skip(1) consumes the replayed null), and only afterwards does
        // RestoreTabs assign the restored active tab.
        //
        // The existing RestoreTabs test only asserts the tab was selected — never
        // that a single log line arrived — so the whole restore-at-startup path was
        // untested. Symptom being reproduced: on app open the active tab shows
        // "0 lines" and no loading overlay, meaning StartTailingAsync never ran.
        var sut = CreateViewModel();
        var path = CreateLogFile("restore-load.log");
        File.WriteAllLines(path, new[] { "r1", "r2", "r3" });

        sut.RestoreTabs(new[] { path }, path);

        var loaded = await WaitUntilAsync(
            () => sut.SelectedTab?.LogEvents.Count >= 3,
            TimeSpan.FromSeconds(30));

        loaded.Should().BeTrue(
            "RestoreTabs must start tailing; the SelectedTab observer's Skip(1) must not swallow the restore emission");
        sut.SelectedTab!.Status.Should().Be("tailing");
    }

    [AvaloniaFact]
    public void ClearCommand_WhenExecuted_EmptiesSelectedTabEvents()
    {
        var sut = CreateViewModel();
        var tab = new TabViewModel(CreateLogFile());
        sut.Tabs.Add(tab);
        sut.SelectedTab = tab;
        tab.AddLogEvent(EnrichedLogEventFactory.CreateSample());

        sut.ClearCommand.Execute().Subscribe();

        tab.LogEvents.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void AddTab_WhenCalled_CreatesAndSelectsTab()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("first.log");

        sut.AddTab(path);

        sut.Tabs.Should().ContainSingle();
        sut.SelectedTab.Should().NotBeNull();
        sut.SelectedTab!.FilePath.Should().Be(path);
        sut.SelectedTab.FileName.Should().Be("first.log");
    }

    [AvaloniaFact]
    public void AddTab_WhenSamePathAlreadyOpen_DoesNotCreateDuplicate()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile();
        sut.AddTab(path);

        sut.AddTab(path);

        sut.Tabs.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void CloseTab_WhenCalled_RemovesTabAndSelectsNext()
    {
        var sut = CreateViewModel();
        var path1 = CreateLogFile("a.log");
        var path2 = CreateLogFile("b.log");
        sut.AddTab(path1);
        sut.AddTab(path2);
        var first = sut.Tabs[0];
        var second = sut.Tabs[1];
        sut.SelectedTab = first;

        sut.CloseTab(first);

        sut.Tabs.Should().ContainSingle().Which.Should().Be(second);
        sut.SelectedTab.Should().Be(second);
    }

    [AvaloniaFact]
    public void SelectedTabStatus_WhenNoTabSelected_IsEmpty()
    {
        var sut = CreateViewModel();

        sut.SelectedTabStatus.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void SelectedTabStatus_WhenTabSelected_ContainsFilePath()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("status.log");
        sut.AddTab(path);

        sut.SelectedTabStatus.Should().NotBeEmpty();
        sut.SelectedTabStatus.Should().Contain("status.log");
    }

    [AvaloniaFact]
    public void SelectedTabStatus_WhenTabSelected_ContainsExpectedFields()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("buffer.log");
        sut.AddTab(path);

        sut.SelectedTabStatus.Should().Contain("buffer.log");
        sut.SelectedTabStatus.Should().Contain("0 lines/s");
    }

    [AvaloniaFact]
    public void BufferCapacity_ReflectsConfiguredValue()
    {
        var sut = CreateViewModel();

        // Default fallback when settings has no value: 50,000.
        sut.BufferCapacity.Should().BeGreaterThan(0);
    }

    [AvaloniaFact]
    public void AddTab_AttachesLinesPerSecondCounter_NewTabStartsAtZero()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("rate.log");

        sut.AddTab(path);

        // No events have arrived yet — rolling 1-second rate should be zero.
        sut.SelectedTab!.LinesPerSecond.Should().Be(0);
    }

    [AvaloniaFact]
    public void SelectedTabStatus_WhenIdle_AlwaysShowsRateField()
    {
        // Per UX decision: rate should always be present (even as 0) so the
        // status bar doesn't have a missing field when no events flow.
        var sut = CreateViewModel();
        var path = CreateLogFile("idle.log");
        sut.AddTab(path);

        sut.SelectedTabStatus.Should().Contain("0 lines/s");
    }

    [AvaloniaFact]
    public void SelectedTabStatus_WhenModifiedUnknown_ShowsEmDashPlaceholder()
    {
        // If file stat could not be read (LastModified == default), the
        // status bar should show an em-dash instead of an empty field that
        // produces a double-bullet gap. This is a sentinel meaning "unknown",
        // not a fake value.
        var sut = CreateViewModel();
        var path = CreateLogFile("no-modified.log");
        sut.AddTab(path);
        var tab = sut.SelectedTab!;

        // Simulate stat read failure: TabViewModel default is already
        // default(DateTime), but ctor populates from FileInfo when possible.
        // Force it to default to assert the rendering path.
        tab.LastModified = default;

        sut.SelectedTabStatus.Should().Contain("—");
        // No double-pipe gap should appear in the rendered string.
        sut.SelectedTabStatus.Should().NotContain("|  |");
    }

    [AvaloniaFact]
    public void CloseTab_DisposesRateSubscription_DoesNotLeak()
    {
        // No observable leak test possible without a mock subject, so we
        // verify the public surface: closing a tab removes it cleanly and
        // doesn't throw. The Dispose path inside CloseTab is exercised.
        var sut = CreateViewModel();
        var path = CreateLogFile("dispose.log");
        sut.AddTab(path);
        var tab = sut.Tabs[0];

        var act = () => sut.CloseTab(tab);

        act.Should().NotThrow();
        sut.Tabs.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void AddTab_WhenPreviousSourceFinishesLoading_DoesNotWriteHistoryIntoNewTab()
    {
        var firstSource = new ControllableLogSource();
        var secondSource = new ControllableLogSource();
        var factory = new QueueLogSourceFactory(firstSource, secondSource);
        var sut = CreateViewModel(factory);
        var firstPath = Path.Combine(_tempDir, "first.log");
        var secondPath = Path.Combine(_tempDir, "second.log");

        sut.AddTab(firstPath);
        Dispatcher.UIThread.RunJobs();
        firstSource.Emit("FIRST");
        Thread.Sleep(500);
        Dispatcher.UIThread.RunJobs();

        Task.Run(firstSource.CompleteInitialLoad).GetAwaiter().GetResult();
        sut.AddTab(secondPath);
        Dispatcher.UIThread.RunJobs();
        secondSource.Emit("SECOND");
        Thread.Sleep(500);
        Dispatcher.UIThread.RunJobs();
        secondSource.CompleteInitialLoad();
        Dispatcher.UIThread.RunJobs();

        sut.Tabs[0].LogEvents.Select(item => item.Raw.Line).Should().Equal("FIRST");
        sut.SelectedTab!.LogEvents.Select(item => item.Raw.Line).Should().Equal("SECOND");
    }

    [AvaloniaFact]
    public void AddTab_WhenPreviousSourceEmitsAfterSwitch_DoesNotWriteIntoNewTab()
    {
        var firstSource = new ControllableLogSource();
        var secondSource = new ControllableLogSource();
        var factory = new QueueLogSourceFactory(firstSource, secondSource);
        var sut = CreateViewModel(factory);
        var firstPath = Path.Combine(_tempDir, "first-live.log");
        var secondPath = Path.Combine(_tempDir, "second-live.log");

        sut.AddTab(firstPath);
        SpinWait.SpinUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return firstSource.IsRunning;
        }, TimeSpan.FromSeconds(2)).Should().BeTrue();
        firstSource.CompleteInitialLoad();
        firstSource.Emit("FIRST");
        SpinWait.SpinUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return sut.Tabs[0].LogEvents.Count == 1;
        }, TimeSpan.FromSeconds(2)).Should().BeTrue();

        sut.AddTab(secondPath);
        firstSource.Emit("STALE");
        SpinWait.SpinUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return secondSource.IsRunning;
        }, TimeSpan.FromSeconds(2)).Should().BeTrue();
        secondSource.CompleteInitialLoad();
        secondSource.Emit("SECOND");
        Thread.Sleep(500);
        Dispatcher.UIThread.RunJobs();

        sut.Tabs[0].LogEvents.Select(item => item.Raw.Line).Should().Equal("FIRST");
        sut.SelectedTab!.LogEvents.Select(item => item.Raw.Line).Should().Equal("SECOND");
    }

    [AvaloniaFact]
    public async Task StartTailingAsync_AfterInitialLogLoaded_RendersAllHistoricalEvents()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("hist-render.log");
        var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
        File.WriteAllLines(path, lines);

        sut.AddTab(path);

        // Wait until initial load finishes and renders to the tab
        var rendered = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count >= 20, TimeSpan.FromSeconds(30));

        rendered.Should().BeTrue();
        sut.SelectedTab!.LogEvents.Count.Should().Be(20);
        sut.SelectedTab.Status.Should().Be("tailing");
    }

    [AvaloniaFact]
    public async Task StartTailingAsync_WithManyHistoricalLines_RendersAll()
    {
        // Repro: loading stuck on large files (InitialLogLoaded races the UI buffer).
        var sut = CreateViewModel();
        var path = Path.Combine(_tempDir, "many.log");
        var lines = Enumerable.Range(1, 50_000)
            .Select(i => $"line {i} - some padding to make lines realistic and longer than a few chars")
            .ToList();
        File.WriteAllLines(path, lines);

        sut.AddTab(path);

        var rendered = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count >= 50_000, TimeSpan.FromSeconds(60));

        rendered.Should().BeTrue();
        sut.SelectedTab!.LogEvents.Count.Should().Be(50_000);
        sut.SelectedTab.Status.Should().Be("tailing");
    }

    [AvaloniaFact]
    public async Task StartTailingAsync_WhenHistoricalExceedsCapacity_CapsToBufferCapacity()
    {
        var sut = CreateViewModel();
        var path = Path.Combine(_tempDir, "capped-hist.log");
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i}").ToList();
        File.WriteAllLines(path, lines);

        sut.AddTab(path);

        var rendered = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count > 0, TimeSpan.FromSeconds(30));
        rendered.Should().BeTrue();
        sut.SelectedTab!.LogEvents.Count.Should().BeLessThanOrEqualTo(sut.BufferCapacity);
        sut.SelectedTab.Status.Should().Be("tailing");
    }

    [AvaloniaFact]
    public async Task StartTailingAsync_WithVeryManyHistoricalLines_RendersCappedToLimit()
    {
        // Regression: the 406MB opencode.log case. Historical lines beyond the
        // buffer capacity must be dropped so UI memory stays bounded.
        var sut = CreateViewModel();
        var path = Path.Combine(_tempDir, "huge.log");
        var lines = Enumerable.Range(1, 100_000)
            .Select(i => $"line {i} - padding to make line realistic in size for tailing")
            .ToList();
        File.WriteAllLines(path, lines);

        sut.AddTab(path);

        var rendered = await WaitUntilAsync(
            () => sut.SelectedTab?.LogEvents.Count >= sut.BufferCapacity,
            TimeSpan.FromSeconds(60));

        rendered.Should().BeTrue($"expected {sut.BufferCapacity} lines rendered, got {sut.SelectedTab?.LogEvents.Count}");
        sut.SelectedTab!.LogEvents.Count.Should().Be(sut.BufferCapacity);
        sut.SelectedTab.Status.Should().Be("tailing");
    }

    [AvaloniaFact]
    public async Task StartTailingAsync_AfterInitialLoad_AppendedLinesRenderLive()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("live-render.log");
        File.WriteAllText(path, "hist1\nhist2\n");

        sut.AddTab(path);

        var histLoaded = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count == 2, TimeSpan.FromSeconds(30));
        histLoaded.Should().BeTrue();

        await File.AppendAllTextAsync(path, "live1\n");

        var liveLoaded = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count == 3, TimeSpan.FromSeconds(30));
        liveLoaded.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task StopCurrentSourceAsync_WhenSwitchingTabs_ClearsPendingBuffer()
    {
        var sut = CreateViewModel();
        var path1 = CreateLogFile("tab1.log");
        var path2 = CreateLogFile("tab2.log");

        sut.AddTab(path1);
        await WaitUntilAsync(() => sut.SelectedTab?.Status == "tailing");

        sut.AddTab(path2);
        await WaitUntilAsync(() => sut.SelectedTab?.Status == "tailing");

        sut.SelectedTab!.FilePath.Should().Be(path2);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }

            if (predicate())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private MainWindowViewModel CreateViewModel(ILogSourceFactory? sourceFactory = null)
    {
        var logger = new LogTail.Core.Logging.ConsoleLogger();
        var settings = new Core.Persistence.SettingsStore(_tempDir, logger);
        sourceFactory ??= new LogSourceFactory(logger);

        return new MainWindowViewModel(settings, sourceFactory);
    }

    private string CreateLogFile(string name = "test.log")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "hello\nworld\n");
        return path;
    }

    private string CreateWorkspace(string name)
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "app.log"), "hello\nworld\n");
        return path;
    }

    [AvaloniaFact]
    public void OpenFolderTab_WhenFolderNotOpen_AddsFolderTabAndSelectsIt()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-new");

        sut.OpenFolderTab(folder);

        sut.Tabs.Should().ContainSingle();
        sut.Tabs[0].IsFolder.Should().BeTrue();
        sut.SelectedTab.Should().BeSameAs(sut.Tabs[0]);
        sut.SelectedTab!.FilePath.Should().Be(folder);
        // Labelled with parent so same-named workspaces stay distinguishable.
        sut.SelectedTab.FileName.Should().Be($"ws-new ({new DirectoryInfo(_tempDir).Name})");
    }

    [AvaloniaFact]
    public void OpenFolderTab_WhenFolderAlreadyOpen_SelectsExistingTabWithoutDuplicating()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-again");
        sut.OpenFolderTab(folder);
        var folderTab = sut.SelectedTab;

        sut.AddTab(CreateLogFile("other.log"));
        sut.OpenFolderTab(folder);

        sut.Tabs.Count(t => t.IsFolder).Should().Be(1);
        sut.SelectedTab.Should().BeSameAs(folderTab);
    }

    [AvaloniaFact]
    public void OpenFolderTab_WhenFolderDoesNotExist_DoesNothing()
    {
        var sut = CreateViewModel();

        var act = () => sut.OpenFolderTab(Path.Combine(_tempDir, "not-there"));

        act.Should().NotThrow();
        sut.Tabs.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task SelectingFolderTab_StopsTailingAndDoesNotStartANewSource()
    {
        var sut = CreateViewModel();
        sut.AddTab(CreateLogFile("app.log"));
        var tailing = await WaitUntilAsync(() => sut.CurrentSource is not null, TimeSpan.FromSeconds(30));
        tailing.Should().BeTrue("a log tab must start a tail source");

        sut.OpenFolderTab(CreateWorkspace("ws-select"));

        var stopped = await WaitUntilAsync(() => sut.CurrentSource is null, TimeSpan.FromSeconds(30));
        stopped.Should().BeTrue("selecting a folder tab must stop the log tail source");
        sut.SelectedTab!.IsTailing.Should().BeFalse();
    }

    [AvaloniaFact]
    public void RestoreWorkspaces_SkipsFoldersThatNoLongerExist()
    {
        var sut = CreateViewModel();
        var present = CreateWorkspace("ws-present");
        var missing = CreateWorkspace("ws-missing");
        Directory.Delete(missing, recursive: true);

        sut.RestoreWorkspaces(new[] { present, missing });

        sut.Tabs.Where(t => t.IsFolder).Select(t => t.FilePath).Should().Equal(present);
    }

    [AvaloniaFact]
    public void HasWorkspaces_TracksWhetherAnyFolderTabIsOpen()
    {
        var sut = CreateViewModel();

        sut.HasWorkspaces.Should().BeFalse();

        sut.OpenFolderTab(CreateWorkspace("ws-flag"));

        sut.HasWorkspaces.Should().BeTrue();
    }

    [AvaloniaFact]
    public void ClosingAnotherFolderTab_KeepsTheActiveFolderSelected()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-a"));
        sut.OpenFolderTab(CreateWorkspace("ws-b"));
        var active = sut.SelectedTab;

        sut.CloseTab(sut.Tabs.First(t => !ReferenceEquals(t, active)));

        PumpUntil(() => sut.HasWorkspaces);
        sut.HasWorkspaces.Should().BeTrue();
        sut.SelectedWorkspaceTab.Should().BeSameAs(active);
        sut.SelectedTab.Should().BeSameAs(active);
    }

    [AvaloniaFact]
    public void CloseTab_WhenFolderTabClosed_LeavesLogTabsAlone()
    {
        var sut = CreateViewModel();
        sut.AddTab(CreateLogFile("keep.log"));
        var logTab = sut.SelectedTab;
        sut.OpenFolderTab(CreateWorkspace("ws-drop"));
        var folderTab = sut.SelectedTab;

        sut.CloseTab(folderTab!);

        sut.Tabs.Should().ContainSingle().Which.Should().BeSameAs(logTab);
        sut.WorkspaceTabs.Should().BeEmpty();
        sut.WorkspaceFiles.Should().BeEmpty();
        sut.SelectedWorkspaceTab.Should().BeNull();
    }

    [AvaloniaFact]
    public void SelectingFolderTab_WhoseFolderWasDeleted_ReportsTheFailure()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-gone");
        sut.OpenFolderTab(folder);
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        Directory.Delete(folder, recursive: true);

        // Re-select the same folder tab so the walk runs against a missing root.
        sut.SelectedTab = null;
        sut.SelectedTab = sut.Tabs[0];

        PumpUntil(() => sut.StatusMessage.StartsWith("Folder not found", StringComparison.Ordinal));
        sut.StatusMessage.Should().Contain(folder);
    }

    [AvaloniaFact]
    public void SelectingFolderTab_WithNoWalkError_ShowsPlainWorkspaceStatus()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-status"));
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);

        sut.StatusMessage.Should().StartWith("Workspace: ws-status");
    }

    [AvaloniaFact]
    public void RestoreWorkspaces_WhenNothingElseSelected_SelectsTheFirstFolder()
    {
        var sut = CreateViewModel();
        var first = CreateWorkspace("ws-r1");
        var second = CreateWorkspace("ws-r2");

        sut.RestoreWorkspaces(new[] { first, second });

        // Otherwise the window comes up with tabs but nothing selected: no
        // content, and the empty state is suppressed.
        sut.SelectedTab.Should().BeSameAs(sut.Tabs[0]);
        sut.SelectedTab!.FilePath.Should().Be(first);
    }

    [AvaloniaFact]
    public void SwitchingFoldersQuickly_ShowsOnlyTheLastFoldersFiles()
    {
        // Two scans in flight: the slower one belongs to a folder that is no
        // longer listed, so its result must be dropped rather than rendered.
        var sut = CreateViewModel();
        var slow = CreateWorkspace("ws-slow");
        for (var i = 0; i < 300; i++)
        {
            File.WriteAllText(Path.Combine(slow, $"line{i:D3}.log"), "x");
        }

        var quick = CreateWorkspace("ws-quick");
        File.WriteAllText(Path.Combine(quick, "only.log"), "x");

        sut.OpenFolderTab(slow);
        sut.OpenFolderTab(quick);

        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        sut.SelectedWorkspaceTab!.FileName.Should().StartWith("ws-quick");
        sut.WorkspaceFiles.Select(f => f.Name).Should().Equal("app.log", "only.log");

        // Give the abandoned scan every chance to land late.
        PumpFor(TimeSpan.FromSeconds(1));
        sut.WorkspaceFiles.Select(f => f.Name).Should().Equal("app.log", "only.log");
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenFolderTabSelected_ShowsTheLogInsideThatFolderTab()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-preview");
        sut.OpenFolderTab(folder);
        var folderTab = sut.SelectedTab;

        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));

        // Single click previews inside the folder tab: no new tab, and the
        // log view is fed by the folder tab's own preview.
        sut.Tabs.Should().ContainSingle();
        sut.SelectedTab.Should().BeSameAs(folderTab);
        sut.ActiveLogTab.Should().NotBeNull();
        sut.ActiveLogTab!.FilePath.Should().EndWith("app.log");
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        sut.ActiveLogTab!.LogEvents.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_AgainWithAnotherFile_ReplacesThePreview()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-preview2");
        File.WriteAllText(Path.Combine(folder, "second.log"), "other\n");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);

        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "second.log"));

        PumpUntil(() => sut.ActiveLogTab!.FilePath.EndsWith("second.log", StringComparison.Ordinal));
        sut.Tabs.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void HasWorkspaceOverflow_WhenTheFolderFitsTheCap_IsFalse()
    {
        // The "N file lain tidak ditampilkan" footer must stay hidden unless
        // files were actually left out.
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-small"));

        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        sut.WorkspaceOverflowCount.Should().Be(0);
        sut.HasWorkspaceOverflow.Should().BeFalse();
    }

    [AvaloniaFact]
    public void SwitchingBackToAFolderBeforeItsScanLands_KeepsAWatcherOnIt()
    {
        // Regression: one flag decided both "already scanned" and "already
        // watched", and it was only set when a scan *landed*. A -> B -> A
        // therefore left A selected with no watcher and a sidebar that never
        // updated again.
        var sut = CreateViewModel();
        var first = CreateWorkspace("ws-watch-a");
        sut.OpenFolderTab(first);
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);

        var second = CreateWorkspace("ws-watch-b");
        sut.OpenFolderTab(second);
        sut.OpenFolderTab(first);

        sut.WatchedFolder.Should().Be(first);
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenBothEntryPointsFire_StartsOneSource()
    {
        // One click reaches ShowWorkspaceFileAsync twice: the SelectedItem
        // write-back and the pointer handler. The same-path guard used to be
        // read before the first await, so both passed it and two tail sources
        // ended up on the same tab.
        var sources = new List<ControllableLogSource>
        {
            new(), new(), new(),
        };
        var factory = new QueueLogSourceFactory(sources.Cast<ILogSource>().ToArray());
        var sut = CreateViewModel(factory);
        var folder = CreateWorkspace("ws-once");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        sources[0].CompleteInitialLoad();
        sources[0].Emit("line");
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);

        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));

        // One source for the preview, and no extra for the repeats.
        sut.CurrentSource.Should().BeSameAs(sources[0]);
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_ReplacingThePreview_ReleasesTheOldOne()
    {
        // Every preview registers a rate window keyed by its own TabViewModel.
        // Without releasing the old one, each clicked file leaks a tab model
        // plus its log buffer for the process lifetime.
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-replace");
        File.WriteAllText(Path.Combine(folder, "second.log"), "other\n");
        sut.OpenFolderTab(folder);

        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "second.log"));
        PumpUntil(() => sut.ActiveLogTab!.FilePath.EndsWith("second.log", StringComparison.Ordinal));

        sut.RateSubscriptionCount.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenTheFileWasDeleted_SaysSo()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-gone-file");
        var path = Path.Combine(folder, "app.log");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(path);
        File.Delete(path);

        await sut.ShowWorkspaceFileAsync(path);

        sut.StatusMessage.Should().Contain("not found");
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenALogTabIsSelected_PointsThatTabAtTheClickedFile()
    {
        // A log tab is a viewer slot, not a pinned file: a single click in the
        // sidebar retargets it, which is what makes the sidebar useful while a
        // log is on screen.
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-retarget");
        File.WriteAllText(Path.Combine(folder, "other.log"), "other\n");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);

        sut.AddTab(CreateLogFile("first.log"));
        PumpUntil(() => sut.ActiveLogTab is not null && sut.ActiveLogTab!.LogEvents.Count > 0);
        var logTab = sut.SelectedTab;

        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "other.log"));

        PumpUntil(() => sut.ActiveLogTab!.FilePath.EndsWith("other.log", StringComparison.Ordinal));
        sut.Tabs.Count(t => !t.IsFolder).Should().Be(1);
        logTab!.FilePath.Should().EndWith("other.log");
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        sut.ActiveLogTab!.LogEvents.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenRetargeting_ForgetsThePreviousFileLines()
    {
        var sut = CreateViewModel();
        var first = CreateLogFile("first-lines.log");
        sut.AddTab(first);
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        var second = CreateLogFile("second-lines.log");

        await sut.ShowWorkspaceFileAsync(second);

        PumpUntil(() => sut.ActiveLogTab!.FilePath.EndsWith("second-lines.log", StringComparison.Ordinal));
        sut.ActiveLogTab!.LineCount.Should().Be(0);
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        sut.ActiveLogTab!.LogEvents.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public async Task ShowWorkspaceFile_WhenTheClickedFileIsAlreadyOpen_DoesNothing()
    {
        var sut = CreateViewModel();
        var path = CreateLogFile("same.log");
        sut.AddTab(path);
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        var before = sut.ActiveLogTab!.LogEvents.Count;

        await sut.ShowWorkspaceFileAsync(path);

        sut.ActiveLogTab!.LogEvents.Count.Should().BeGreaterThanOrEqualTo(before);
        sut.Tabs.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void ActiveLogTab_WhenLogTabSelected_IsThatTab()
    {
        var sut = CreateViewModel();
        var logPath = CreateLogFile("plain.log");

        sut.AddTab(logPath);

        sut.ActiveLogTab.Should().BeSameAs(sut.SelectedTab);
    }

    [AvaloniaFact]
    public void ActiveLogTab_WhenFolderTabHasNoPreview_IsNull()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-nopreview"));

        sut.ActiveLogTab.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task ClosingTheFolderTab_StopsThePreviewSource()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-preview3");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);
        var folderTab = sut.SelectedTab;

        sut.CloseTab(folderTab!);

        PumpUntil(() => sut.CurrentSource is null);
        sut.CurrentSource.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task SwitchingAwayFromAPreviewingFolderTab_TailsTheNewTabInstead()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-preview4");
        sut.OpenFolderTab(folder);
        await sut.ShowWorkspaceFileAsync(Path.Combine(folder, "app.log"));
        PumpUntil(() => sut.ActiveLogTab!.LogEvents.Count > 0);

        sut.AddTab(CreateLogFile("other.log"));

        PumpUntil(() => sut.ActiveLogTab!.FilePath.EndsWith("other.log", StringComparison.Ordinal));
        sut.ActiveLogTab!.FilePath.Should().EndWith("other.log");
    }

    [AvaloniaFact]
    public void SelectingFolderTab_ListsItsLogFilesInTheSidebar()
    {
        var sut = CreateViewModel();
        var folder = CreateWorkspace("ws-list");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "deep.log"), "x\n");

        sut.OpenFolderTab(folder);

        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        sut.WorkspaceFiles.Select(f => f.Name).Should().BeEquivalentTo("app.log", "deep.log");
        sut.WorkspaceFiles.Single(f => f.Name == "deep.log").Path
            .Should().Be(Path.Combine(folder, "nested", "deep.log"));
    }

    [AvaloniaFact]
    public void SelectingAnotherFolderTab_ReplacesTheListedFiles()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-a"));
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);

        sut.OpenFolderTab(CreateWorkspace("ws-b"));

        PumpUntil(() => sut.WorkspaceFiles.Count == 1 && sut.SelectedWorkspaceTab!.FileName.StartsWith("ws-b", StringComparison.Ordinal));
        sut.SelectedWorkspaceTab!.FileName.Should().StartWith("ws-b");
    }

    [AvaloniaFact]
    public void SelectingALogTab_KeepsTheSidebarOnTheLastFolder()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-keep"));
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        var folderTab = sut.SelectedWorkspaceTab;

        // Navigating to another log must not empty the navigator.
        sut.AddTab(CreateLogFile("other.log"));

        PumpUntil(() => sut.SelectedTab is { IsFolder: false });
        sut.SelectedWorkspaceTab.Should().BeSameAs(folderTab);
        sut.WorkspaceFiles.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public void ClosingTheListedFolderTab_FallsBackToAnotherFolderTab()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-x"));
        sut.OpenFolderTab(CreateWorkspace("ws-y"));
        var listed = sut.SelectedWorkspaceTab;
        sut.OpenFolderTab(CreateWorkspace("ws-z"));
        PumpUntil(() => sut.SelectedWorkspaceTab!.FileName.StartsWith("ws-z", StringComparison.Ordinal));

        // Close the folder whose files the sidebar was listing, without
        // selecting it first.
        sut.Tabs.Remove(listed!);

        PumpUntil(() => sut.SelectedWorkspaceTab is not null && !ReferenceEquals(sut.SelectedWorkspaceTab, listed));
        sut.SelectedWorkspaceTab.Should().NotBeNull();

        // The list is filled by a background scan, so wait for the scan rather
        // than asserting the moment the selection moved.
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);
        sut.WorkspaceFiles.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    public void ClosingTheLastFolderTab_EmptiesTheSidebar()
    {
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-only"));
        PumpUntil(() => sut.WorkspaceFiles.Count > 0);

        sut.CloseTab(sut.SelectedTab!);

        PumpUntil(() => sut.WorkspaceFiles.Count == 0);
        sut.WorkspaceFiles.Should().BeEmpty();
        sut.HasWorkspaces.Should().BeFalse();
    }

    [AvaloniaFact]
    public void WorkspaceFiles_WhenFolderIsEmpty_StaysEmptyWithoutError()
    {
        var sut = CreateViewModel();
        var empty = Path.Combine(_tempDir, "ws-empty");
        Directory.CreateDirectory(empty);

        sut.OpenFolderTab(empty);

        PumpUntil(() => sut.StatusMessage.StartsWith("Workspace:", StringComparison.Ordinal));
        sut.WorkspaceFiles.Should().BeEmpty();
        sut.StatusMessage.Should().NotContain("not found");
    }

    [AvaloniaFact]
    public void ToggleWorkspaceSort_SortsFilesAndUpdatesLabels()
    {
        var sut = CreateViewModel();
        var folder = Path.Combine(_tempDir, "ws-sort-test");
        Directory.CreateDirectory(folder);
        var file1 = Path.Combine(folder, "b.log");
        var file2 = Path.Combine(folder, "a.log");
        File.WriteAllText(file1, "1234567890\n"); // 11 bytes
        File.WriteAllText(file2, "1\n"); // 2 bytes
        File.SetLastWriteTime(file1, DateTime.Now.AddHours(-1));
        File.SetLastWriteTime(file2, DateTime.Now);

        sut.OpenFolderTab(folder);
        PumpUntil(() => sut.WorkspaceFiles.Count >= 2);

        // Initial default: Name Ascending
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Name);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Ascending);
        sut.WorkspaceSortNameLabel.Should().Be("Name ↑");
        sut.WorkspaceFiles[0].Name.Should().Be("a.log");

        // Toggle Name -> Descending
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Name);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);
        sut.WorkspaceSortNameLabel.Should().Be("Name ↓");
        sut.WorkspaceFiles[0].Name.Should().Be("b.log");

        // Switch to Size -> Defaults to Descending (largest first)
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);
        sut.WorkspaceSortSizeLabel.Should().Be("Size ↓");
        sut.WorkspaceFiles[0].Name.Should().Be("b.log");

        // Toggle Size -> Ascending (smallest first)
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Ascending);
        sut.WorkspaceSortSizeLabel.Should().Be("Size ↑");
        sut.WorkspaceFiles[0].Name.Should().Be("a.log");

        // Switch to Date -> Defaults to Descending (newest first)
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Date);
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Date);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);
        sut.WorkspaceSortDateLabel.Should().Be("Date ↓");
        sut.WorkspaceFiles[0].Name.Should().Be("a.log");

        // Toggle Date -> Ascending (oldest first)
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Date);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Ascending);
        sut.WorkspaceSortDateLabel.Should().Be("Date ↑");
        sut.WorkspaceFiles[0].Name.Should().Be("b.log");
    }

    [AvaloniaFact]
    public void SortCommand_ExecutesToggleWorkspaceSort()
    {
        // The sidebar buttons bind Command="{Binding SortCommand}", so exercising
        // the command is exercising the button path — no window needed.
        var sut = CreateViewModel();
        var folder = Path.Combine(_tempDir, "ws-cmd-sort");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "b.log"), "12345\n");
        File.WriteAllText(Path.Combine(folder, "a.log"), "1\n");

        sut.OpenFolderTab(folder);
        PumpUntil(() => sut.WorkspaceFiles.Count >= 2);

        sut.SortCommand.Execute(WorkspaceSortCriterion.Size).Subscribe();

        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);
        sut.WorkspaceFiles[0].Name.Should().Be("b.log");
    }

    [AvaloniaFact]
    public void SortCommand_RaisesWorkspaceSortChanged()
    {
        // SessionTracker hooks this event for immediate session.json writes, so
        // a crash right after sorting still restores the order.
        var sut = CreateViewModel();
        sut.OpenFolderTab(CreateWorkspace("ws-cmd-event"));

        var fired = 0;
        sut.WorkspaceSortChanged += () => fired++;

        sut.SortCommand.Execute(WorkspaceSortCriterion.Date).Subscribe();

        fired.Should().Be(1);
    }

    [AvaloniaFact]
    public void MainWindow_LoadsWithSortButtonsBound()
    {
        // Guards the sidebar XAML (Command + CommandParameter bindings).
        // Compiled bindings fail at load time, so just loading the view with a
        // workspace open proves the wiring resolves.
        Splat.Locator.CurrentMutable.Register(
            () => new Avalonia.ReactiveUI.AvaloniaActivationForViewFetcher(),
            typeof(ReactiveUI.IActivationForViewFetcher));

        var vm = CreateViewModel();
        vm.OpenFolderTab(CreateWorkspace("ws-xaml-sort"));
        PumpUntil(() => vm.WorkspaceFiles.Count > 0);

        var window = new MainWindow { DataContext = vm };
        window.Content.Should().NotBeNull();

        window.Close();
    }

    [AvaloniaFact]
    public void WorkspaceSort_IsRememberedPerWorkspace()
    {
        var sut = CreateViewModel();
        var folder1 = Path.Combine(_tempDir, "ws-sort-mem-1");
        var folder2 = Path.Combine(_tempDir, "ws-sort-mem-2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        File.WriteAllText(Path.Combine(folder1, "x.log"), "1");
        File.WriteAllText(Path.Combine(folder2, "y.log"), "1");

        sut.OpenFolderTab(folder1);
        PumpUntil(() => sut.SelectedWorkspaceTab?.FilePath == folder1);

        // Change folder1 to Size Descending
        sut.ToggleWorkspaceSort(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);

        // Open folder2 -> Should have default Name Ascending
        sut.OpenFolderTab(folder2);
        PumpUntil(() => sut.SelectedWorkspaceTab?.FilePath == folder2);
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Name);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Ascending);

        // Switch back to folder1 -> Should restore Size Descending
        sut.SelectedTab = sut.Tabs[0];
        PumpUntil(() => sut.SelectedWorkspaceTab?.FilePath == folder1);
        sut.WorkspaceSortCriterion.Should().Be(WorkspaceSortCriterion.Size);
        sut.WorkspaceSortDirection.Should().Be(WorkspaceSortDirection.Descending);
    }

    /// <summary>
    /// Runs the Avalonia dispatcher until <paramref name="predicate"/> holds.
    /// The selected-tab observer posts its work to the dispatcher, so workspace
    /// state lands asynchronously even though the property set is synchronous.
    /// </summary>
    private static void PumpUntil(Func<bool> predicate, TimeSpan? timeout = null)
    {
        SpinWait.SpinUntil(
            () =>
            {
                Dispatcher.UIThread.RunJobs();
                return predicate();
            },
            timeout ?? TimeSpan.FromSeconds(10))
            .Should().BeTrue("dispatcher did not reach the expected state in time");
    }

    /// <summary>Runs the dispatcher for a fixed span, asserting nothing.</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private sealed class QueueLogSourceFactory(params ILogSource[] sources) : ILogSourceFactory
    {
        private readonly Queue<ILogSource> _sources = new(sources);

        public ILogSource CreateFileSource(string filePath, int initialWindowBytes, int maxWindowBytes, int tailLineLimit) => _sources.Dequeue();
    }

    private sealed class ControllableLogSource : ILogSource
    {
        private readonly Subject<RawLogEvent> _events = new();

        public string DisplayName => "controllable.log";
        public IObservable<RawLogEvent> Events => _events;
        public bool IsRunning { get; private set; }
        public long FileSize => 0;

        public event Action? InitialLogLoaded;

        public Task StartAsync(CancellationToken ct)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public ValueTask StopAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void SeekToOffset(long offset)
        {
        }

        public void Emit(string line)
        {
            _events.OnNext(new RawLogEvent(DateTimeOffset.UtcNow, DisplayName, 0, line, true));
        }

        public void CompleteInitialLoad()
        {
            InitialLogLoaded?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            _events.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class EnrichedLogEventFactory
{
    public static EnrichedLogEvent CreateSample() =>
        new(
            new RawLogEvent(DateTimeOffset.UtcNow, "test", 0, "line"),
            LogLevel.Unknown,
            Timestamp: null,
            LevelColorKey: null);
}
