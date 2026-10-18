# Large File Memory Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bound memory consumption when opening large log files to O(TailLineLimit) by reading only the trailing window from the end of the file, fixing the UI eviction bug on initial flush, and removing transient LOH allocations via pooled buffers and span-based line scanning.

**Architecture:** `FileTailSource.DrainInitialContentAsync` seeks backward to `length - currentWindow` (starting at `InitialWindowBytes`, expanding up to `MaxWindowBytes` if needed) and uses a bounded ring queue to collect only the last `TailLineLimit` lines. In the UI, `MainWindowViewModel.FlushPendingHistorical` slices incoming historical snapshots to `TailLineLimit` and enforces eviction correctly so `tab.LogEvents` never exceeds capacity. Transient string decoding and buffer allocations are replaced with `ArrayPool<byte>.Shared` and span slicing.

**Tech Stack:** .NET 10, C# 14, Avalonia 11, ReactiveUI, System.Buffers (`ArrayPool<byte>`), xUnit + FluentAssertions.

## Global Constraints

- Target framework: `net10.0`. Do not change it.
- Test framework is xUnit with FluentAssertions (`result.Should()...`); follow existing test style in `tests/LogTail.Core.Tests` and `tests/LogTail.UI.Tests`.
- One type per file. Do not combine classes/interfaces in a single file.
- **Do NOT commit per task.** The user's standing rule is a single commit after ALL code is written and tests pass (see Task 6). Ignore the usual "commit each task" habit.
- Never chain shell commands with `&&`; run each command as its own step.
- Never write machine-specific absolute paths (`<drive>:\Users\...`, `/Users/...`, `/home/...`) or the user's name into tracked files. In tests use temp dirs via `Path.GetTempPath()` and portable fake paths.
- After editing any `.cs` file, if Rider MCP tools are available, run `post_edit_quality_check` via `rider_execute_tool`. Otherwise the build in the task's verification step covers it.
- Before the final commit, present a code review verdict and run the project hygiene scan (Task 6).

---

### Task 1: Configuration Models — `AppSettings` & `ILogSourceFactory` updates

**Files:**
- Modify: `src/LogTail.Core/Models/AppSettings.cs`
- Modify: `src/LogTail.Core/Sources/ILogSourceFactory.cs`
- Modify: `src/LogTail.Core/Sources/LogSourceFactory.cs`
- Test: `tests/LogTail.Core.Tests/Persistence/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `AppSettings` with `TailLineLimit` (int, default 50_000), `InitialWindowBytes` (int, default 8 MB), `MaxWindowBytes` (int, default 64 MB).
  - `ILogSourceFactory.CreateFileSource(string filePath, int tailLineLimit, int initialWindowBytes, int maxWindowBytes)`.
  - `LogSourceFactory.CreateFileSource(...)`.

- [ ] **Step 1: Write the failing tests**

Append these test cases inside `tests/LogTail.Core.Tests/Persistence/SettingsStoreTests.cs`:

```csharp
    [Fact]
    public void Load_WhenFileDoesNotExist_DefaultWindowLimitsMatchSpec()
    {
        var sut = new SettingsStore(_tempDir);
        var settings = sut.Load();

        settings.TailLineLimit.Should().Be(50_000);
        settings.InitialWindowBytes.Should().Be(8 * 1024 * 1024);
        settings.MaxWindowBytes.Should().Be(64 * 1024 * 1024);
    }

    [Fact]
    public void Save_WhenCustomLimitsConfigured_RoundtripsSuccessfully()
    {
        var sut = new SettingsStore(_tempDir);
        var custom = new AppSettings(
            TailLineLimit: 10_000,
            InitialWindowBytes: 4 * 1024 * 1024,
            MaxWindowBytes: 32 * 1024 * 1024);

        sut.Save(custom);

        var loaded = sut.Load();
        loaded.TailLineLimit.Should().Be(10_000);
        loaded.InitialWindowBytes.Should().Be(4 * 1024 * 1024);
        loaded.MaxWindowBytes.Should().Be(32 * 1024 * 1024);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~DefaultWindowLimitsMatchSpec"`
Expected: FAIL — `AppSettings` has no `TailLineLimit` property (compiler error).

- [ ] **Step 3: Update `AppSettings`**

Replace `src/LogTail.Core/Models/AppSettings.cs`:

```csharp
namespace LogTail.Core.Models;

public sealed record AppSettings(
    ThemeMode Theme = ThemeMode.System,
    int BufferCapacity = 50_000,
    int MaxBufferCapacity = 2_000_000,
    TimeSpan PollInterval = default,
    string DefaultEncoding = "utf-8",
    bool RestoreLastSession = true,
    int TailLineLimit = 50_000,
    int InitialWindowBytes = 8 * 1024 * 1024,
    int MaxWindowBytes = 64 * 1024 * 1024);
```

- [ ] **Step 4: Update `ILogSourceFactory` and `LogSourceFactory`**

Replace `src/LogTail.Core/Sources/ILogSourceFactory.cs`:

```csharp
namespace LogTail.Core.Sources;

public interface ILogSourceFactory
{
    ILogSource CreateFileSource(
        string filePath,
        int tailLineLimit = 50_000,
        int initialWindowBytes = 8 * 1024 * 1024,
        int maxWindowBytes = 64 * 1024 * 1024);
}
```

Replace `src/LogTail.Core/Sources/LogSourceFactory.cs`:

```csharp
using LogTail.Core.Logging;

namespace LogTail.Core.Sources;

public sealed class LogSourceFactory : ILogSourceFactory
{
    private readonly ILogTailLogger _logger;
    private readonly TimeSpan _pollInterval;

    public LogSourceFactory(ILogTailLogger logger, TimeSpan pollInterval = default)
    {
        _logger = logger;
        _pollInterval = pollInterval == default ? TimeSpan.FromMilliseconds(250) : pollInterval;
    }

    public ILogSource CreateFileSource(
        string filePath,
        int tailLineLimit = 50_000,
        int initialWindowBytes = 8 * 1024 * 1024,
        int maxWindowBytes = 64 * 1024 * 1024)
    {
        return new FileTailSource(
            filePath,
            _pollInterval,
            _logger,
            tailLineLimit,
            initialWindowBytes,
            maxWindowBytes);
    }
}
```

- [ ] **Step 5: Run tests to verify Task 1 passes**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS.

---

### Task 2: Core — Windowed Initial Load in `FileTailSource`

**Files:**
- Modify: `src/LogTail.Core/Sources/FileTailSource.cs`
- Test: `tests/LogTail.Core.Tests/Sources/FileTailSourceTests.cs`

**Interfaces:**
- Consumes: `tailLineLimit`, `initialWindowBytes`, `maxWindowBytes` parameters in `FileTailSource` constructor.
- Produces: `DrainInitialContentAsync` implementing backward-seek windowing + span line parsing + `ArrayPool<byte>.Shared`.

- [ ] **Step 1: Write failing unit tests in `FileTailSourceTests.cs`**

Append these test cases to `tests/LogTail.Core.Tests/Sources/FileTailSourceTests.cs`:

```csharp
    [Fact]
    public async Task StartAsync_WhenFileLargerThanLineLimit_EmitsOnlyTailLines()
    {
        var filePath = Path.Combine(_tempDir, "large-tail.log");
        var allLines = Enumerable.Range(1, 1000).Select(i => $"line-{i:D4}").ToList();
        await File.WriteAllLinesAsync(filePath, allLines);

        // Limit to only 50 trailing lines, window 10KB
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
        // 100 lines of 200 chars = ~20KB total
        var lines = Enumerable.Range(1, 100)
            .Select(i => $"line-{i:D3}-" + new string('x', 200))
            .ToList();
        await File.WriteAllLinesAsync(filePath, lines);

        // Initial window only 512 bytes (holds ~2 lines), should expand to get all 100 lines
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~StartAsync_WhenFileLargerThanLineLimit_EmitsOnlyTailLines"`
Expected: FAIL — `FileTailSource` constructor does not accept `tailLineLimit`.

- [ ] **Step 3: Update `FileTailSource` implementation**

Replace `src/LogTail.Core/Sources/FileTailSource.cs` with the windowed backwards-seeking implementation:

```csharp
using System.Buffers;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using LogTail.Core.Logging;
using LogTail.Core.Models;

namespace LogTail.Core.Sources;

public sealed class FileTailSource : ILogSource
{
    private readonly string _filePath;
    private readonly TimeSpan _pollInterval;
    private readonly ILogTailLogger _logger;
    private readonly int _tailLineLimit;
    private readonly int _initialWindowBytes;
    private readonly int _maxWindowBytes;
    private readonly Subject<RawLogEvent> _events = new();
    private readonly object _readLock = new();

    private FileStream? _stream;
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private CancellationTokenSource? _cts;
    private long _offset;
    private Task? _readLoop;
    private volatile bool _forceReopen;
    private readonly StringBuilder _partialLineBuffer = new();
    private long _fileSizeAtStart;
    private bool _initialLogLoadedRaised;
    private event Action? _initialLogLoaded;

    public FileTailSource(
        string filePath,
        TimeSpan pollInterval,
        ILogTailLogger logger,
        int tailLineLimit = 50_000,
        int initialWindowBytes = 8 * 1024 * 1024,
        int maxWindowBytes = 64 * 1024 * 1024)
    {
        _filePath = filePath;
        _pollInterval = pollInterval == default ? TimeSpan.FromMilliseconds(250) : pollInterval;
        _logger = logger;
        _tailLineLimit = tailLineLimit > 0 ? tailLineLimit : 50_000;
        _initialWindowBytes = initialWindowBytes > 0 ? initialWindowBytes : 8 * 1024 * 1024;
        _maxWindowBytes = maxWindowBytes >= _initialWindowBytes ? maxWindowBytes : _initialWindowBytes;
    }

    public string DisplayName => Path.GetFileName(_filePath);

    public IObservable<RawLogEvent> Events => _events.AsObservable();

    public bool IsRunning { get; private set; }

    public event Action? InitialLogLoaded
    {
        add => _initialLogLoaded += value;
        remove => _initialLogLoaded -= value;
    }

    public long FileSize => _fileSizeAtStart;

    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        _fileSizeAtStart = new FileInfo(_filePath).Length;

        // Set up FileSystemWatcher
        var dir = Path.GetDirectoryName(_filePath)!;
        var filter = Path.GetFileName(_filePath);

        _watcher = new FileSystemWatcher(dir, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;

        _watcher.EnableRaisingEvents = true;

        await DrainInitialContentAsync(_cts.Token).ConfigureAwait(false);

        _pollTimer = new Timer(PollCallback, null, _pollInterval, _pollInterval);

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

        IsRunning = true;
        _logger.Debug($"Started tailing: {_filePath}");
    }

    public async ValueTask StopAsync()
    {
        if (!IsRunning) return;

        IsRunning = false;

        _pollTimer?.Dispose();
        _pollTimer = null;

        _watcher?.Dispose();
        _watcher = null;

        _cts?.Cancel();

        if (_readLoop != null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _stream?.Dispose();
        _stream = null;

        _cts?.Dispose();
        _cts = null;

        _partialLineBuffer.Clear();
        _initialLogLoaded = null;
        _initialLogLoadedRaised = false;

        _logger.Debug($"Stopped tailing: {_filePath}");
    }

    public void SeekToOffset(long offset)
    {
        _offset = offset;
        _initialLogLoadedRaised = true;
        _partialLineBuffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _events.Dispose();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        TriggerRead();
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.Warn($"File deleted: {e.FullPath}. Marking for reopen.");
        _forceReopen = true;
        TriggerRead();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _logger.Warn($"File rotated: {e.OldFullPath} → {e.FullPath}");
        _forceReopen = true;
        TriggerRead();
    }

    private void PollCallback(object? state)
    {
        TriggerRead();
    }

    private void TriggerRead()
    {
        lock (_readLock)
        {
            Monitor.Pulse(_readLock);
        }
    }

    private async Task DrainInitialContentAsync(CancellationToken ct)
    {
        if (_initialLogLoadedRaised || _stream == null || !File.Exists(_filePath))
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        var fileLength = new FileInfo(_filePath).Length;

        if (fileLength == 0)
        {
            _offset = 0;
            _initialLogLoadedRaised = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(50).ConfigureAwait(false);
                _initialLogLoaded?.Invoke();
            });
            return;
        }

        long currentWindow = Math.Min(fileLength, _initialWindowBytes);
        var collectedLines = new Queue<string>(_tailLineLimit);

        while (!ct.IsCancellationRequested)
        {
            long startOffset = fileLength - currentWindow;
            _stream.Seek(startOffset, SeekOrigin.Begin);

            int bytesToRead = (int)currentWindow;
            byte[] rented = ArrayPool<byte>.Shared.Rent(bytesToRead);
            int totalBytesRead = 0;

            try
            {
                while (totalBytesRead < bytesToRead)
                {
                    int read = await _stream.ReadAsync(
                        rented.AsMemory(totalBytesRead, bytesToRead - totalBytesRead),
                        ct).ConfigureAwait(false);

                    if (read <= 0) break;
                    totalBytesRead += read;
                }

                collectedLines.Clear();
                ExtractLinesFromSpan(
                    new ReadOnlySpan<byte>(rented, 0, totalBytesRead),
                    startOffset > 0, // isPartialAtStart
                    _tailLineLimit,
                    collectedLines);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            // If we have enough lines or already read the entire file or hit the max window ceiling
            if (collectedLines.Count >= _tailLineLimit ||
                currentWindow >= fileLength ||
                currentWindow >= _maxWindowBytes)
            {
                break;
            }

            // Expand window
            long nextWindow = Math.Min(fileLength, Math.Min(currentWindow * 2, _maxWindowBytes));
            if (nextWindow <= currentWindow)
            {
                break;
            }
            currentWindow = nextWindow;
        }

        int totalEmitted = 0;
        foreach (var line in collectedLines)
        {
            totalEmitted++;
            _events.OnNext(new RawLogEvent(
                ReadAt: DateTimeOffset.UtcNow,
                SourceId: _filePath,
                FileOffset: fileLength,
                Line: line,
                IsHistorical: true));
        }

        _offset = fileLength;
        _partialLineBuffer.Clear();

        sw.Stop();
        _logger.Info($"[InitialLoad:Core] File: '{Path.GetFileName(_filePath)}', Size: {fileLength:N0} bytes, Lines Emitted: {totalEmitted:N0}, Read Time: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalSeconds:F2}s)");

        _initialLogLoadedRaised = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            _initialLogLoaded?.Invoke();
        });
    }

    private static void ExtractLinesFromSpan(
        ReadOnlySpan<byte> span,
        bool isPartialAtStart,
        int maxLines,
        Queue<string> output)
    {
        var current = span;

        // If this window does not start at byte 0, skip the first partial line
        if (isPartialAtStart)
        {
            int firstNewline = current.IndexOf((byte)'\n');
            if (firstNewline >= 0)
            {
                current = current.Slice(firstNewline + 1);
            }
            else
            {
                return;
            }
        }

        while (!current.IsEmpty)
        {
            int nextNewline = current.IndexOf((byte)'\n');
            ReadOnlySpan<byte> lineBytes;

            if (nextNewline >= 0)
            {
                lineBytes = current.Slice(0, nextNewline);
                current = current.Slice(nextNewline + 1);
            }
            else
            {
                lineBytes = current;
                current = ReadOnlySpan<byte>.Empty;
            }

            // Strip trailing \r
            if (!lineBytes.IsEmpty && lineBytes[^1] == (byte)'\r')
            {
                lineBytes = lineBytes.Slice(0, lineBytes.Length - 1);
            }

            if (!lineBytes.IsEmpty)
            {
                var lineStr = Encoding.UTF8.GetString(lineBytes);
                if (output.Count >= maxLines)
                {
                    output.Dequeue();
                }
                output.Enqueue(lineStr);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                lock (_readLock)
                {
                    Monitor.Wait(_readLock, _pollInterval);
                }

                if (_forceReopen)
                {
                    _forceReopen = false;
                    await ReopenFileAsync().ConfigureAwait(false);
                    if (_stream == null) continue;
                }
                else if (_stream == null || !File.Exists(_filePath))
                {
                    await ReopenFileAsync().ConfigureAwait(false);
                    if (_stream == null) continue;
                }

                var currentLength = new FileInfo(_filePath).Length;

                if (currentLength < _offset)
                {
                    _logger.Warn($"File truncated (size {currentLength} < offset {_offset}). Resetting offset.");
                    _offset = 0;
                    _partialLineBuffer.Clear();
                }

                if (_stream.Position != _offset)
                {
                    _stream.Seek(_offset, SeekOrigin.Begin);
                }

                while (_offset < currentLength && !ct.IsCancellationRequested)
                {
                    var bufferSize = (int)Math.Min(currentLength - _offset, 64 * 1024);
                    byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);

                    int bytesRead;
                    try
                    {
                        bytesRead = await _stream.ReadAsync(rented.AsMemory(0, bufferSize), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        // We copy out to text before returning buffer
                    }

                    if (bytesRead <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        break;
                    }

                    _offset += bytesRead;
                    var text = Encoding.UTF8.GetString(rented, 0, bytesRead);
                    ArrayPool<byte>.Shared.Return(rented);

                    _partialLineBuffer.Append(text);

                    var combined = _partialLineBuffer.ToString();
                    var lines = combined.Split(["\r\n", "\n"], StringSplitOptions.None);

                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i];
                        if (!string.IsNullOrEmpty(line))
                        {
                            _events.OnNext(new RawLogEvent(
                                ReadAt: DateTimeOffset.UtcNow,
                                SourceId: _filePath,
                                FileOffset: _offset,
                                Line: line,
                                IsHistorical: false));
                        }
                    }

                    _partialLineBuffer.Clear();
                    _partialLineBuffer.Append(lines[^1]);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"Read error on {_filePath}", ex);
                await ReopenFileAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReopenFileAsync()
    {
        _stream?.Dispose();
        _stream = null;

        for (int i = 0; i < 20; i++)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    _stream = new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        FileOptions.Asynchronous);

                    _offset = 0;
                    _partialLineBuffer.Clear();
                    _logger.Info($"Reopened file: {_filePath} at offset 0");
                    return;
                }
                catch
                {
                    // Retry
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        _logger.Warn($"File not found after rotation: {_filePath}. Will keep retrying.");
    }
}
```

- [ ] **Step 4: Run Core tests to verify Task 2 passes**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj`
Expected: All tests PASS.

---

### Task 3: UI Layer — Fix `FlushPendingHistorical` & Wire `TailLineLimit`

**Files:**
- Modify: `src/LogTail.UI/ViewModels/MainWindowViewModel.cs`
- Test: `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `AppSettings.TailLineLimit`, `AppSettings.InitialWindowBytes`, `AppSettings.MaxWindowBytes`.
- Produces: `MainWindowViewModel` with bounded historical flush and fixed capacity eviction.

- [ ] **Step 1: Update UI tests for capped behavior**

In `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`:
1. Add test `StartTailingAsync_WhenHistoricalExceedsCapacity_CapsToBufferCapacity`:
```csharp
    [Fact]
    public async Task StartTailingAsync_WhenHistoricalExceedsCapacity_CapsToBufferCapacity()
    {
        var sut = CreateViewModel();
        var path = Path.Combine(_tempDir, "capped-hist.log");
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i}").ToList();
        File.WriteAllLines(path, lines);

        sut.AddTab(path);

        var rendered = await WaitUntilAsync(() => sut.SelectedTab?.LogEvents.Count > 0, TimeSpan.FromSeconds(30));
        rendered.Should().BeTrue();
        sut.SelectedTab!.LogEvents.Count.Should().BeLessOrEqualTo(sut.BufferCapacity);
        sut.SelectedTab.Status.Should().Be("tailing");
    }
```
2. Update the existing `StartTailingAsync_WithVeryManyHistoricalLines_RendersAll` to assert capped behavior at `sut.BufferCapacity` (50k lines) instead of expecting 1,000,000:
```csharp
    [Fact]
    public async Task StartTailingAsync_WithVeryManyHistoricalLines_RendersCappedToLimit()
    {
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
```

- [ ] **Step 2: Update `MainWindowViewModel.cs`**

Modify `MainWindowViewModel.cs` to pass limits to factory and fix `FlushPendingHistorical`:
1. In `StartTailingAsync`:
```csharp
        var loadedSettings = _settings.Load();
        var source = _sourceFactory.CreateFileSource(
            tab.FilePath,
            loadedSettings.TailLineLimit,
            loadedSettings.InitialWindowBytes,
            loadedSettings.MaxWindowBytes);
```
2. In `FlushPendingHistorical`:
```csharp
    private void FlushPendingHistorical(List<EnrichedLogEvent> snapshot)
    {
        var tab = SelectedTab;
        if (tab is null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        tab.Status = "tailing";

        // If snapshot alone exceeds buffer capacity, keep only the trailing lines
        IReadOnlyList<EnrichedLogEvent> toAdd = snapshot;
        if (toAdd.Count > _buffer.Capacity)
        {
            toAdd = snapshot.Skip(snapshot.Count - _buffer.Capacity).ToList();
        }

        var excess = (tab.LogEvents.Count + toAdd.Count) - _buffer.Capacity;
        if (excess > 0)
        {
            tab.EvictFromFront(excess);
        }

        foreach (var item in toAdd)
        {
            _buffer.Add(item);
        }

        tab.AddLogEvents(toAdd);
        RecordLinesForRate(tab, toAdd.Count);

        lock (_historicalLock)
        {
            _pendingHistoricalEvents.Clear();
            _historicalFlushed = true;
        }

        sw.Stop();
        _logger?.Info($"[InitialLoad:UI] File: '{Path.GetFileName(tab.FilePath)}', Lines Rendered: {toAdd.Count:N0}, UI Flush Time: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalSeconds:F2}s)");
    }
```

- [ ] **Step 3: Run UI tests to verify Task 3 passes**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj`
Expected: All tests PASS.

---

### Task 4: Full Solution Test Verification

**Files:**
- Test all: `tests/LogTail.Core.Tests`, `tests/LogTail.UI.Tests`

- [ ] **Step 1: Run complete test suite across solution**

Run: `dotnet test`
Expected: 100% tests PASS with zero failures.

---

### Task 5: Code Review & Project Hygiene Verification

- [ ] **Step 1: Run Project Hygiene Audit**

Verify no absolute local file paths or user identifiers were introduced into tracked source or test files:
Run: `git grep -i -E "[A-Za-z]:[\\/]+Users|/Users/|/home/"`
Expected: No matches in any tracked source/doc/config files.

- [ ] **Step 2: Review git status and git diff**

Inspect `git status` and `git diff` to ensure only intended changes are staged.

---

### Task 6: Final Commit

- [ ] **Step 1: Create single atomic commit**

Run:
1. `git add .`
2. `git commit -m "perf(core,ui): optimize large file initial load with windowed tailing and bounded memory"`
