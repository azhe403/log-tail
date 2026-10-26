using System.Reactive.Linq;
using FluentAssertions;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Sources;
using Xunit;

namespace LogTail.Core.Tests.Sources;

public sealed class FileTailSourceTests : IAsyncLifetime
{
    private readonly string _tempDir;

    public FileTailSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // Polls `predicate` every 25ms up to `timeout`, returning true as soon as it holds.
    // Makes timing-sensitive integration tests deterministic under load.
    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    [Fact]
    public async Task StartAsync_WhenNewLinesAppended_EmitsAppendedEvents()
    {
        var filePath = Path.Combine(_tempDir, "test.log");
        File.WriteAllText(filePath, "line1\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => true); // allow tail to settle

        await File.AppendAllTextAsync(filePath, "line2\nline3\n");

        var sawAll = await WaitUntilAsync(() =>
            events.Any(e => e.Line.Contains("line2")) && events.Any(e => e.Line.Contains("line3")));

        await sut.StopAsync();

        sawAll.Should().BeTrue("the two appended lines should both be emitted");
    }

    [Fact]
    public async Task StartAsync_WhenExistingFileTailed_EmitsExistingAndAppendedEvents()
    {
        var filePath = Path.Combine(_tempDir, "existing.log");
        File.WriteAllText(filePath, "old-line1\nold-line2\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);

        var sawOld = await WaitUntilAsync(() =>
            events.Any(e => e.Line == "old-line1") && events.Any(e => e.Line == "old-line2"));

        await File.AppendAllTextAsync(filePath, "new-line\n");

        var sawNew = await WaitUntilAsync(() => events.Any(e => e.Line == "new-line"));

        await sut.StopAsync();

        sawOld.Should().BeTrue("existing lines before start should be emitted");
        sawNew.Should().BeTrue("the newly appended line should also be emitted");
    }

    [Fact]
    public async Task StartAsync_WhenFileExceedsMaxInitialLines_TailsOnlyLastNLines()
    {
        var filePath = Path.Combine(_tempDir, "exceeds_max.log");
        var lines = Enumerable.Range(1, 100).Select(i => $"line-{i}").ToList();
        await File.WriteAllLinesAsync(filePath, lines);

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger(), tailLineLimit: 10);

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);

        var completed = await WaitUntilAsync(() => events.Count >= 10);

        await sut.StopAsync();

        completed.Should().BeTrue();
        events.Should().HaveCount(10);
        events.First().Line.Should().Be("line-91");
        events.Last().Line.Should().Be("line-100");
    }

    [Fact]
    public async Task StartAsync_WhenFileRotated_ReopensAndEmitsNewEvents()
    {
        var filePath = Path.Combine(_tempDir, "rotate.log");
        await File.WriteAllTextAsync(filePath, "before-rotate\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => true);

        // Simulate rotation: delete original, create new file with same name
        File.Delete(filePath);
        await File.AppendAllTextAsync(filePath, "after-rotate\n");

        var sawNew = await WaitUntilAsync(() => events.Any(e => e.Line == "after-rotate"));

        await sut.StopAsync();

        sawNew.Should().BeTrue("rotating to a new file with the same name should be detected and re-read");
    }

    [Fact]
    public async Task StopAsync_WhenInvoked_StopsEmittingEvents()
    {
        var filePath = Path.Combine(_tempDir, "stop.log");
        await File.WriteAllTextAsync(filePath, "start\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await sut.StopAsync();

        var countBefore = events.Count;

        await File.AppendAllTextAsync(filePath, "after-stop\n");
        await Task.Delay(300);

        events.Count.Should().Be(countBefore);
    }

    [Fact]
    public async Task DisplayName_WhenInitialized_ReturnsFileName()
    {
        var filePath = Path.Combine(_tempDir, "mylog.log");
        await File.WriteAllTextAsync(filePath, "");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        sut.DisplayName.Should().Be("mylog.log");
    }

    [Fact]
    public async Task StartAsync_WhenFileHasHistoricalLines_EmitsThemWithIsHistoricalTrue()
    {
        var filePath = Path.Combine(_tempDir, "historical.log");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\nline5\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);

        var sawAll = await WaitUntilAsync(() => events.Count >= 5);

        await sut.StopAsync();

        sawAll.Should().BeTrue();
        events.Should().HaveCount(5);
        events.Should().OnlyContain(e => e.IsHistorical);
    }

    [Fact]
    public async Task StartAsync_AfterInitialLoad_RaisesInitialLogLoadedEventOnce()
    {
        var filePath = Path.Combine(_tempDir, "initial-loaded.log");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        int initialLogLoadedCount = 0;
        sut.InitialLogLoaded += () => Interlocked.Increment(ref initialLogLoadedCount);

        await sut.StartAsync(CancellationToken.None);

        var raised = await WaitUntilAsync(() => initialLogLoadedCount > 0);

        await sut.StopAsync();

        raised.Should().BeTrue();
        initialLogLoadedCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_AfterInitialLoad_AppendedLinesHaveIsHistoricalFalse()
    {
        var filePath = Path.Combine(_tempDir, "appended-historical.log");
        await File.WriteAllTextAsync(filePath, "initial1\ninitial2\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        var loadedTcs = new TaskCompletionSource<bool>();
        sut.InitialLogLoaded += () => loadedTcs.TrySetResult(true);

        await sut.StartAsync(CancellationToken.None);

        var loaded = await WaitUntilAsync(() => loadedTcs.Task.IsCompleted);
        loaded.Should().BeTrue();

        await File.AppendAllTextAsync(filePath, "new-appended-line\n");

        var sawNew = await WaitUntilAsync(() => events.Any(e => e.Line == "new-appended-line"));

        await sut.StopAsync();

        sawNew.Should().BeTrue();
        var initialEvents = events.Where(e => e.Line.StartsWith("initial")).ToList();
        var newEvents = events.Where(e => e.Line == "new-appended-line").ToList();

        initialEvents.Should().OnlyContain(e => e.IsHistorical);
        newEvents.Should().OnlyContain(e => !e.IsHistorical);
    }

    [Fact]
    public async Task StartAsync_WhenFileRotated_DoesNotRaiseInitialLogLoadedAgain()
    {
        var filePath = Path.Combine(_tempDir, "rotate-event.log");
        await File.WriteAllTextAsync(filePath, "before-rotate\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        int initialLogLoadedCount = 0;
        sut.InitialLogLoaded += () => Interlocked.Increment(ref initialLogLoadedCount);

        await sut.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => initialLogLoadedCount == 1);

        // Rotate file: delete and recreate
        File.Delete(filePath);
        await File.AppendAllTextAsync(filePath, "after-rotate\n");

        // Wait to allow rotation detection and read
        await Task.Delay(300);

        await sut.StopAsync();

        initialLogLoadedCount.Should().Be(1, "rotation should not re-raise InitialLogLoaded");
    }

    [Fact]
    public async Task StartAsync_WhenFileEmpty_ImmediatelyRaisesInitialLogLoaded()
    {
        var filePath = Path.Combine(_tempDir, "empty.log");
        await File.WriteAllTextAsync(filePath, "");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        int initialLogLoadedCount = 0;
        sut.InitialLogLoaded += () => Interlocked.Increment(ref initialLogLoadedCount);

        await sut.StartAsync(CancellationToken.None);

        var raised = await WaitUntilAsync(() => initialLogLoadedCount > 0);

        await sut.StopAsync();

        raised.Should().BeTrue();
        initialLogLoadedCount.Should().Be(1);
    }

    [Fact]
    public async Task FileSize_AfterStart_ReturnsCorrectSize()
    {
        var filePath = Path.Combine(_tempDir, "size.log");
        var content = new string('a', 1024);
        await File.WriteAllTextAsync(filePath, content);

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        await sut.StartAsync(CancellationToken.None);

        sut.FileSize.Should().Be(1024);

        await sut.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenCalled_AllowsReuseOfSource()
    {
        var filePath = Path.Combine(_tempDir, "reuse.log");
        await File.WriteAllTextAsync(filePath, "line1\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        int run1Count = 0;
        sut.InitialLogLoaded += () => Interlocked.Increment(ref run1Count);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => run1Count == 1);
        await sut.StopAsync();

        run1Count.Should().Be(1);

        int run2Count = 0;
        sut.InitialLogLoaded += () => Interlocked.Increment(ref run2Count);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => run2Count == 1);
        await sut.StopAsync();

        run2Count.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WhenFileLargerThanLineLimit_EmitsOnlyTailLines()
    {
        var filePath = Path.Combine(_tempDir, "large-tail.log");
        var allLines = Enumerable.Range(1, 1000).Select(i => $"line-{i:D4}").ToList();
        await File.WriteAllLinesAsync(filePath, allLines);

        await using var sut = new FileTailSource(
            filePath,
            TimeSpan.FromMilliseconds(50),
            new ConsoleLogger(),
            tailLineLimit: 50,
            initialWindowBytes: 10 * 1024,
            maxWindowBytes: 50 * 1024);

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);
        var loaded = await WaitUntilAsync(() => events.Count == 50);

        await sut.StopAsync();

        loaded.Should().BeTrue();
        events.Should().HaveCount(50);
        events.First().Line.Should().Be("line-0951");
        events.Last().Line.Should().Be("line-1000");
        events.Should().OnlyContain(e => e.IsHistorical);
    }

    [Fact]
    public async Task StartAsync_WhenLinesExceedInitialWindow_ExpandsWindowUpToLineLimit()
    {
        var filePath = Path.Combine(_tempDir, "expand-window.log");
        var lines = Enumerable.Range(1, 100)
            .Select(i => $"line-{i:D3}-" + new string('x', 200))
            .ToList();
        await File.WriteAllLinesAsync(filePath, lines);

        await using var sut = new FileTailSource(
            filePath,
            TimeSpan.FromMilliseconds(50),
            new ConsoleLogger(),
            tailLineLimit: 100,
            initialWindowBytes: 512,
            maxWindowBytes: 64 * 1024);

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);
        var loaded = await WaitUntilAsync(() => events.Count == 100);

        await sut.StopAsync();

        loaded.Should().BeTrue();
        events.Should().HaveCount(100);
        events.First().Line.Should().StartWith("line-001-");
        events.Last().Line.Should().StartWith("line-100-");
    }

    [Fact]
    public async Task StartAsync_AfterInitialTailLoad_NewAppendsStreamLive()
    {
        var filePath = Path.Combine(_tempDir, "tail-stream-live.log");
        var lines = Enumerable.Range(1, 500).Select(i => $"hist-{i}").ToList();
        await File.WriteAllLinesAsync(filePath, lines);

        await using var sut = new FileTailSource(
            filePath,
            TimeSpan.FromMilliseconds(50),
            new ConsoleLogger(),
            tailLineLimit: 20,
            initialWindowBytes: 4096,
            maxWindowBytes: 16384);

        var events = new List<RawLogEvent>();
        using var sub = sut.Events.Subscribe(e => events.Add(e));

        await sut.StartAsync(CancellationToken.None);
        var initialLoaded = await WaitUntilAsync(() => events.Count == 20);
        initialLoaded.Should().BeTrue();

        await File.AppendAllTextAsync(filePath, "live-append-1\n");
        var sawLive = await WaitUntilAsync(() => events.Any(e => e.Line == "live-append-1"));

        await sut.StopAsync();

        sawLive.Should().BeTrue();
        events.Count.Should().Be(21);
        events.Last().Line.Should().Be("live-append-1");
        events.Last().IsHistorical.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_CountsEveryLineInFileNotJustTheTail()
    {
        var filePath = Path.Combine(_tempDir, "count-all.log");
        await File.WriteAllLinesAsync(filePath, Enumerable.Range(1, 1_000).Select(i => $"line-{i}"));

        await using var sut = new FileTailSource(
            filePath,
            TimeSpan.FromMilliseconds(50),
            new ConsoleLogger(),
            tailLineLimit: 10);

        await sut.StartAsync(CancellationToken.None);

        var counted = await WaitUntilAsync(
            () => sut.TotalLineCount == 1_000,
            TimeSpan.FromSeconds(10));

        await sut.StopAsync();

        counted.Should().BeTrue($"expected 1,000 lines, got {sut.TotalLineCount}");
    }

    [Fact]
    public async Task StartAsync_WhenFileHasNoTrailingNewline_CountsTheLastLine()
    {
        var filePath = Path.Combine(_tempDir, "no-final-newline.log");
        await File.WriteAllTextAsync(filePath, "one\ntwo\nthree");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        await sut.StartAsync(CancellationToken.None);

        var counted = await WaitUntilAsync(() => sut.TotalLineCount == 3, TimeSpan.FromSeconds(10));

        await sut.StopAsync();

        counted.Should().BeTrue($"expected 3 lines, got {sut.TotalLineCount}");
    }

    [Fact]
    public async Task StartAsync_CountsBlankLinesThatTheEventPipelineDrops()
    {
        var filePath = Path.Combine(_tempDir, "blank-lines.log");
        await File.WriteAllTextAsync(filePath, "one\n\n\nfour\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        await sut.StartAsync(CancellationToken.None);

        var counted = await WaitUntilAsync(() => sut.TotalLineCount == 4, TimeSpan.FromSeconds(10));

        await sut.StopAsync();

        counted.Should().BeTrue($"expected 4 lines, got {sut.TotalLineCount}");
    }

    [Fact]
    public async Task StartAsync_WhenLinesAppended_TotalLineCountGrows()
    {
        var filePath = Path.Combine(_tempDir, "count-live.log");
        await File.WriteAllTextAsync(filePath, "one\ntwo\n");

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        await sut.StartAsync(CancellationToken.None);

        var initial = await WaitUntilAsync(() => sut.TotalLineCount == 2, TimeSpan.FromSeconds(10));
        initial.Should().BeTrue($"expected the initial count, got {sut.TotalLineCount}");

        await File.AppendAllTextAsync(filePath, "three\n");

        var grew = await WaitUntilAsync(() => sut.TotalLineCount == 3, TimeSpan.FromSeconds(10));

        await sut.StopAsync();

        grew.Should().BeTrue($"expected 3 lines after the append, got {sut.TotalLineCount}");
    }

    [Fact]
    public async Task StartAsync_WhenFileIsEmpty_ReportsZeroLines()
    {
        var filePath = Path.Combine(_tempDir, "empty.log");
        await File.WriteAllTextAsync(filePath, string.Empty);

        await using var sut = new FileTailSource(filePath, TimeSpan.FromMilliseconds(50), new ConsoleLogger());

        await sut.StartAsync(CancellationToken.None);

        var counted = await WaitUntilAsync(() => sut.TotalLineCount == 0, TimeSpan.FromSeconds(10));

        await sut.StopAsync();

        counted.Should().BeTrue($"expected 0 lines, got {sut.TotalLineCount}");
    }
}
