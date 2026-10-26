using System.Buffers;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using LogTail.Core.Indexing;
using LogTail.Core.Logging;
using LogTail.Core.Models;

namespace LogTail.Core.Sources;

public sealed class FileTailSource : ILogSource
{
    private readonly string _filePath;
    private readonly TimeSpan _pollInterval;
    private readonly ILogTailLogger _logger;
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
    private readonly int _tailLineLimit;
    private readonly int _initialWindowBytes;
    private readonly int _maxWindowBytes;
    private long _fileSizeAtStart;
    private bool _initialLogLoadedRaised;
    private event Action? _initialLogLoaded;

    // True line count, counted from byte zero once at attach and kept current
    // from the bytes the read loop sees afterwards. -1 means "not known yet";
    // the UI falls back to its rendered count until the first scan completes.
    private readonly FileLineCounter _lineCounter = new();
    private readonly object _lineCountGate = new();
    private long _lineCount = -1;
    private long _baseNewlines;
    private long _baseByteOffset;
    private bool _baseEndsWithNewline;
    private bool _baseApplied;
    private long _liveNewlines;
    private bool _hasLiveBytes;
    private bool _liveEndsWithNewline;
    private CancellationTokenSource? _lineScanCts;
    private Task? _lineScan;
    private int _lineCountGeneration;

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

    public long? TotalLineCount
    {
        get
        {
            var count = Volatile.Read(ref _lineCount);
            return count < 0 ? null : count;
        }
    }

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

        if (!_initialLogLoadedRaised)
        {
            _offset = 0;
        }
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
        StartBaseLineScan(_offset);

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

        _lineScanCts?.Cancel();
        if (_lineScan != null)
        {
            try
            {
                await _lineScan.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        _lineScanCts?.Dispose();
        _lineScanCts = null;
        _lineScan = null;

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
        // Jump past historical content so the source only emits appends that
        // happened after the caller already loaded what it needed. Caller is
        // responsible for marking the source as past the initial-load phase.
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
                    startOffset > 0,
                    _tailLineLimit,
                    collectedLines);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            if (collectedLines.Count >= _tailLineLimit ||
                currentWindow >= fileLength ||
                currentWindow >= _maxWindowBytes)
            {
                break;
            }

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
                    if (_stream == null)
                    {
                        continue;
                    }
                }
                else if (_stream == null || !File.Exists(_filePath))
                {
                    await ReopenFileAsync().ConfigureAwait(false);
                    if (_stream == null)
                    {
                        continue;
                    }
                }

                var currentLength = new FileInfo(_filePath).Length;

                if (currentLength < _offset)
                {
                    _logger.Warn($"File truncated (size {currentLength} < offset {_offset}). Resetting offset.");
                    _offset = 0;
                    _partialLineBuffer.Clear();
                    ResetLineCount();
                }

                if (_stream.Position != _offset)
                {
                    _stream.Seek(_offset, SeekOrigin.Begin);
                }

                while (_offset < currentLength && !ct.IsCancellationRequested)
                {
                    var bufferSize = (int)Math.Min(currentLength - _offset, 64 * 1024);
                    var buffer = new byte[bufferSize];

                    int bytesRead = await _stream.ReadAsync(buffer, 0, bufferSize, ct).ConfigureAwait(false);

                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    RecordLiveBytes(new ReadOnlySpan<byte>(buffer, 0, bytesRead));
                    _offset += bytesRead;
                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
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

        // Wait for file to reappear
        for (int i = 0; i < 20; i++) // 2 seconds max
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

                    _offset = 0; // Start from beginning of new file
                    _partialLineBuffer.Clear();
                    ResetLineCount();
                    _logger.Info($"Reopened file: {_filePath} at offset 0");
                    return;
                }
                catch
                {
                    // File exists but locked, retry
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        _logger.Warn($"File not found after rotation: {_filePath}. Will keep retrying.");
    }

    /// <summary>
    /// Starts the one-time scan that counts every line from byte zero up to
    /// <paramref name="byteLimit"/>, the offset the live loop will resume from.
    /// The scan runs off the UI thread; the count stays unknown until it ends.
    /// </summary>
    private void StartBaseLineScan(long byteLimit)
    {
        CancellationTokenSource scanCts;
        int generation;
        long limit;

        lock (_lineCountGate)
        {
            if (_baseApplied || _lineScan is { IsCompleted: false })
            {
                return;
            }

            _baseByteOffset = Math.Max(0, byteLimit);
            limit = _baseByteOffset;
            generation = ++_lineCountGeneration;
            _lineScanCts?.Dispose();
            scanCts = _cts is null
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _lineScanCts = scanCts;
        }

        _lineScan = Task.Run(
            () => ScanBaseLineCountAsync(limit, generation, scanCts.Token),
            scanCts.Token);
    }

    private async Task ScanBaseLineCountAsync(long byteLimit, int generation, CancellationToken ct)
    {
        try
        {
            var result = await _lineCounter.CountAsync(_filePath, byteLimit, ct).ConfigureAwait(false);

            lock (_lineCountGate)
            {
                // A truncation, rotation, or stop made this result irrelevant.
                if (generation != _lineCountGeneration)
                {
                    return;
                }

                _baseNewlines = result.NewlineCount;
                _baseEndsWithNewline = result.EndsWithNewline;
                _baseApplied = true;
                Volatile.Write(ref _lineCount, ComputeLineCountLocked());
            }
        }
        catch (OperationCanceledException)
        {
            // StopAsync or a reset cancelled the scan.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Line count failed for {_filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds the newlines in a chunk read by the live loop. Counting bytes
    /// rather than emitted events keeps blank lines in the total, which the
    /// event pipeline drops.
    /// </summary>
    private void RecordLiveBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var newlines = FileLineCounter.CountNewlines(bytes);
        var endsWithNewline = bytes[^1] == (byte)'\n';

        lock (_lineCountGate)
        {
            _liveNewlines += newlines;
            _hasLiveBytes = true;
            _liveEndsWithNewline = endsWithNewline;

            if (_baseApplied)
            {
                Volatile.Write(ref _lineCount, ComputeLineCountLocked());
            }
        }
    }

    /// <summary>
    /// Forgets the known count after a truncation or rotation. The read loop
    /// restarts at byte zero, so the live counter rebuilds the total from
    /// there without a second full scan.
    /// </summary>
    private void ResetLineCount()
    {
        lock (_lineCountGate)
        {
            _lineCountGeneration++;
            _lineScanCts?.Cancel();
            _lineScanCts?.Dispose();
            _lineScanCts = null;
            _lineScan = null;

            _baseNewlines = 0;
            _baseByteOffset = 0;
            _baseEndsWithNewline = false;
            _baseApplied = true;
            _liveNewlines = 0;
            _hasLiveBytes = false;
            _liveEndsWithNewline = false;
            Volatile.Write(ref _lineCount, 0);
        }
    }

    /// <summary>
    /// The count is newlines plus one for a final line left unterminated,
    /// which is how an editor numbers the last line of a file.
    /// </summary>
    private long ComputeLineCountLocked()
    {
        var newlines = _baseNewlines + _liveNewlines;
        var endsWithNewline = _hasLiveBytes ? _liveEndsWithNewline : _baseEndsWithNewline;
        var hasBytes = _baseByteOffset > 0 || _hasLiveBytes;
        return hasBytes && !endsWithNewline ? newlines + 1 : newlines;
    }
}
