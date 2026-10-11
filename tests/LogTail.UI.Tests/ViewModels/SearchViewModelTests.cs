using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using LogTail.Core.Models;
using LogTail.UI.ViewModels;
using Xunit;

namespace LogTail.UI.Tests.ViewModels;

public sealed class SearchViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public SearchViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-search-test-{Guid.NewGuid():N}");
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
    public async Task SearchText_WhenEmpty_HasNoMatchesAndCollapsedPanel()
    {
        var tab = CreateTabWithLines("a.log", "alpha", "beta");
        var sut = CreateViewModel(tab);

        await WaitUntilAsync(() => sut.PanelHeight.Value == 0);

        sut.HasQuery.Should().BeFalse();
        sut.Matches.Should().BeEmpty();
        sut.MatchCountText.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task SearchText_WithQuery_MatchesOnlyTheActiveTab()
    {
        // Off mode is the default: results come from the log on screen, not
        // from every open tab.
        var first = CreateTabWithLines("first.log", "alpha error", "quiet");
        var second = CreateTabWithLines("second.log", "beta error", "quiet");
        var sut = CreateViewModel(first, second);
        sut.SetActiveTab(first);

        sut.SearchText = "error";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.Matches.Should().ContainSingle();
        sut.Matches[0].Tab.Should().BeSameAs(first);
        sut.Matches[0].FileName.Should().Be("first.log");
        sut.Matches[0].LineNumber.Should().Be(1);
        sut.Matches[0].Line.Should().Be("alpha error");
    }

    [AvaloniaFact]
    public async Task SearchText_MatchesCaseInsensitive()
    {
        var tab = CreateTabWithLines("a.log", "ERROR: disk full");
        var sut = CreateViewModel(tab);
        sut.SetActiveTab(tab);

        sut.SearchText = "error";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.Matches.Should().ContainSingle();
        sut.MatchCountText.Should().Be("1 results");
    }

    [AvaloniaFact]
    public async Task SetActiveTab_FollowsTheTabOnScreen()
    {
        var first = CreateTabWithLines("first.log", "alpha", "shared");
        var second = CreateTabWithLines("second.log", "beta", "shared");
        var sut = CreateViewModel(first, second);
        sut.SetActiveTab(first);
        sut.SearchText = "shared";
        await WaitUntilAsync(() => sut.Matches.Count == 1);
        sut.Matches[0].Tab.Should().BeSameAs(first);

        sut.SetActiveTab(second);
        await WaitUntilAsync(() => sut.Matches.Count == 1 && sut.Matches[0].Tab == second);

        sut.Matches.Should().ContainSingle();
        sut.Matches[0].Tab.Should().BeSameAs(second);
    }

    [AvaloniaFact]
    public async Task SearchAllTabs_WhenOn_MatchesEveryOpenTabInTabOrder()
    {
        var first = CreateTabWithLines("first.log", "alpha hit", "quiet");
        var second = CreateTabWithLines("second.log", "beta hit", "quiet");
        var sut = CreateViewModel(first, second);
        sut.SetActiveTab(first);

        sut.SearchText = "hit";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.SearchAllTabs = true;
        await WaitUntilAsync(() => sut.Matches.Count == 2);

        sut.Matches[0].Tab.Should().BeSameAs(first);
        sut.Matches[1].Tab.Should().BeSameAs(second);
        sut.MatchCountText.Should().Be("2 results");
    }

    [AvaloniaFact]
    public async Task FolderTab_SearchesThePreviewedFileAndPointsAtTheFolderTab()
    {
        var folder = new TabViewModel(Path.Combine(_tempDir, "logs"), isFolder: true);
        var preview = CreateTabWithLines("preview.log", "preview hit", "quiet");
        folder.SetPreview(preview);

        var sut = CreateViewModel(folder);
        sut.SetActiveTab(folder);

        sut.SearchText = "hit";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.Matches.Should().ContainSingle();
        sut.Matches[0].Tab.Should().BeSameAs(folder);
        sut.Matches[0].Line.Should().Be("preview hit");
    }

    [AvaloniaFact]
    public async Task MatchCountText_CountsEveryMatch()
    {
        // No display cap: the count is the real total and the list holds every
        // match, so "10,001 results" also scrolls to 10,001 rows.
        var lines = new List<EnrichedLogEvent>();
        for (var i = 0; i < 10_001; i++)
        {
            lines.Add(CreateEvent($"hit number {i}"));
        }
        var tab = new TabViewModel(Path.Combine(_tempDir, "big.log"));
        tab.AddLogEvents(lines);

        var sut = CreateViewModel(tab);
        sut.SetActiveTab(tab);
        sut.SearchText = "hit";
        await WaitUntilAsync(() => sut.Matches.Count == 10_001, TimeSpan.FromSeconds(15));

        sut.MatchCount.Should().Be(10_001);
        sut.MatchCountText.Should().Be($"{10_001:N0} results");
    }

    [AvaloniaFact]
    public async Task JumpToMatch_WhenLinePresent_ActivatesTabAndRequestsScroll()
    {
        var tab = CreateTabWithLines("a.log", "alpha hit");
        var activated = new List<TabViewModel>();
        LogSearchMatch? scrolled = null;

        var sut = new SearchViewModel(new System.Collections.ObjectModel.ObservableCollection<TabViewModel> { tab }, t => activated.Add(t));
        sut.ScrollToLine.RegisterHandler(ctx =>
        {
            scrolled = ctx.Input;
            ctx.SetOutput(true);
            return Task.CompletedTask;
        });
        sut.SetActiveTab(tab);

        sut.SearchText = "hit";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.JumpToMatch(sut.Matches[0]);

        activated.Should().ContainSingle().Which.Should().BeSameAs(tab);
        scrolled.Should().BeSameAs(sut.Matches[0]);
    }

    [AvaloniaFact]
    public void JumpToMatch_WhenLineEvicted_DoesNothing()
    {
        var tab = CreateTabWithLines("a.log", "alpha hit");
        var activated = new List<TabViewModel>();
        var interactionCount = 0;

        var sut = new SearchViewModel(new System.Collections.ObjectModel.ObservableCollection<TabViewModel> { tab }, t => activated.Add(t));
        sut.ScrollToLine.RegisterHandler(ctx =>
        {
            interactionCount++;
            ctx.SetOutput(false);
            return Task.CompletedTask;
        });
        sut.SetActiveTab(tab);

        var match = new LogSearchMatch(tab, tab.LogEvents[0]);
        tab.EvictFromFront(1);

        sut.JumpToMatch(match);

        activated.Should().BeEmpty();
        interactionCount.Should().Be(0);
    }

    [AvaloniaFact]
    public async Task PanelHeight_WithQuery_UsesDefaultAndIgnoresCollapsedWriteBack()
    {
        var tab = CreateTabWithLines("a.log", "alpha");
        var sut = CreateViewModel(tab);
        sut.SetActiveTab(tab);

        sut.SearchText = "alpha";
        await WaitUntilAsync(() => sut.Matches.Count == 1);

        sut.PanelHeight.Value.Should().Be(180);

        // The binding echoes the collapsed zero while the panel is hidden;
        // it must never overwrite the usable height.
        sut.PanelHeight = new Avalonia.Controls.GridLength(0);
        sut.PanelHeight.Value.Should().Be(180);
    }

    [AvaloniaFact]
    public async Task ClosePanel_HidesThePanelButKeepsTheQuery()
    {
        var tab = CreateTabWithLines("a.log", "alpha");
        var sut = CreateViewModel(tab);
        sut.SetActiveTab(tab);

        sut.SearchText = "alpha";
        await WaitUntilAsync(() => sut.Matches.Count == 1);
        sut.ShowPanel.Should().BeTrue();

        sut.ClosePanel();

        // The query and its matches survive; only visibility collapses.
        sut.SearchText.Should().Be("alpha");
        sut.ShowPanel.Should().BeFalse();
        sut.PanelHeight.Value.Should().Be(0);
        sut.Matches.Should().ContainSingle();
    }

    [AvaloniaFact]
    public void OpenPanel_AfterClose_ShowsThePanelAgain()
    {
        var tab = CreateTabWithLines("a.log", "alpha");
        var sut = CreateViewModel(tab);
        sut.SetActiveTab(tab);

        // Typing a query opens the panel synchronously.
        sut.SearchText = "alpha";
        sut.ShowPanel.Should().BeTrue();

        sut.ClosePanel();
        sut.ShowPanel.Should().BeFalse();

        // The next keystroke reopens it.
        sut.SearchText += "a";
        sut.ShowPanel.Should().BeTrue();
        sut.PanelHeight.Value.Should().Be(180);

        // And so does an explicit Ctrl+F-style OpenPanel.
        sut.ClosePanel();
        sut.ShowPanel.Should().BeFalse();
        sut.OpenPanel();
        sut.ShowPanel.Should().BeTrue();
    }

    private static SearchViewModel CreateViewModel(params TabViewModel[] tabs)
    {
        var collection = new System.Collections.ObjectModel.ObservableCollection<TabViewModel>(tabs);
        return new SearchViewModel(collection, _ => { });
    }

    private TabViewModel CreateTabWithLines(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name);
        var tab = new TabViewModel(path);
        var events = new List<EnrichedLogEvent>();
        foreach (var line in lines)
        {
            events.Add(CreateEvent(line));
        }
        tab.AddLogEvents(events);
        return tab;
    }

    private static EnrichedLogEvent CreateEvent(string line) =>
        new(
            new RawLogEvent(DateTimeOffset.UtcNow, "test", line.Length, line),
            LogLevel.Info,
            Timestamp: null,
            LevelColorKey: null);

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
}
