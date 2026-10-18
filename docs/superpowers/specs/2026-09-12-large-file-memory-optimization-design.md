# Large File Memory Optimization Design

**Date:** 2026-09-12
**Status:** Implemented
**Scope:** Core (`LogTail.Core`) + UI (`LogTail.UI`)

## Problem

When opening large log files (e.g. 100k–1M+ lines, hundreds of megabytes), LogTail experiences excessive memory consumption (memory ballooning) caused by four compounding architectural issues:

1. **Unbounded Historical Accumulation:** `FileTailSource.DrainInitialContentAsync` reads the file from byte 0 to EOF and emits every historical line. The UI buffers all historical events in an unbounded `List<EnrichedLogEvent> _pendingHistoricalEvents` until `InitialLogLoaded` fires. For a 1M-line file, peak memory holds 1M allocated `EnrichedLogEvent` objects (~300MB+).
2. **Broken Eviction on Initial Flush:** In `MainWindowViewModel.FlushPendingHistorical`, `tab.EvictFromFront(excess)` is called *before* appending to `tab.LogEvents`. However, during initial load, `tab.LogEvents` is still empty (`Count == 0`), making `EvictFromFront` a no-op (`RemoveFromFront` calls `Clear()` on empty list). As a result, `tab.AddLogEvents(snapshot)` inserts all 1M lines into the UI collection regardless of configured buffer capacity.
3. **Transient Large Object Heap (LOH) Churn:** `DrainInitialContentAsync` reads up to 1MB chunks via `new byte[bufferSize]`, decodes them via `Encoding.UTF8.GetString()`, appends to a `StringBuilder`, calls `_partialLineBuffer.ToString()`, and splits via `Split(["\r\n", "\n"])`. This repeatedly allocates multimegabyte transient strings and arrays, driving GC pressure and keeping the process working set elevated.
4. **Duplicate Object Retention in Unused RingBuffer:** `RingBuffer<EnrichedLogEvent> _buffer` (intended for Milestone 2 search/filter) auto-grows up to `MaxBufferCapacity` (default 2,000,000) and holds duplicate object references for every event across all tabs without having any consumer.

## Goals

1. **Windowed Initial Load (Hybrid Tail Window):** Initial file load must only read and display the trailing tail of the file — bounded by a line limit (`TailLineLimit`, default 50,000) and a backward-seek byte window (`InitialWindowBytes`, default 8 MB, expanding up to `MaxWindowBytes`, default 64 MB).
2. **Strict Memory Upper Bound:** Memory consumption during and after loading any large file (from 1 MB to 10 GB) must remain bounded at O(`TailLineLimit`), roughly ~15–25 MB for typical log lines.
3. **Correct UI Capacity Enforcement:** Fix `FlushPendingHistorical` and `OnNewEventsBatch` so that `tab.LogEvents` never exceeds `TailLineLimit`.
4. **Zero LOH Allocations during Read:** Use `ArrayPool<byte>.Shared` and span-based newline scanning during file read instead of `Encoding.GetString` + `StringBuilder.ToString` + `Split`.
5. **Seamless Tail Continuation:** After the initial tail window is loaded, `_offset` is placed at EOF, and new appended lines flow live into the UI as `IsHistorical: false` without disruption.

## Non-Goals

- Arbitrary backward scrolling to byte 0 for multi-gigabyte files (would require virtualized file-offset indexing, deferred to a future milestone if needed).
- Removing `RingBuffer` entirely (it will be used for M2 search/filter, but its growth should align with `TailLineLimit`).

## Technical Design

### 1. Core Layer: `FileTailSource` Windowed Initial Load

#### Algorithm: `DrainInitialContentAsync`

Given:
- `L = file length in bytes`
- `lineLimit = _tailLineLimit` (default: 50,000)
- `initialWindowBytes = _initialWindowBytes` (default: 8 MB = 8 * 1024 * 1024)
- `maxWindowBytes = _maxWindowBytes` (default: 64 MB = 64 * 1024 * 1024)

Steps:
1. If file is empty (`L == 0`), raise `InitialLogLoaded` and return.
2. Set `currentWindow = Math.Min(L, initialWindowBytes)`.
3. Loop (grow window if needed):
   a. Compute `startOffset = L - currentWindow`.
   b. Read `currentWindow` bytes from `startOffset` into pooled buffer (`ArrayPool<byte>.Shared`).
   c. If `startOffset > 0`:
      - Discard partial line at the beginning by advancing past the first newline (`\n` or `\r\n`).
   d. Scan lines in the remaining span and collect into a temporary sliding ring buffer of size `lineLimit` (`Queue<string>` or circular array).
   e. If collected lines count >= `lineLimit` OR `currentWindow >= L` OR `currentWindow >= maxWindowBytes`:
      - Emit all lines in the sliding buffer in chronological order via `_events.OnNext(new RawLogEvent(..., IsHistorical: true))`.
      - Set `_offset = L` (end of file).
      - Break loop.
   f. Else (`currentWindow < L` and `count < lineLimit` and `currentWindow < maxWindowBytes`):
      - Double `currentWindow = Math.Min(L, Math.Min(currentWindow * 2, maxWindowBytes))`.
      - Retry read from step 3a.
4. Set `_initialLogLoadedRaised = true` and invoke `_initialLogLoaded` event.

#### Span-based Line Scanner

Instead of `Encoding.UTF8.GetString(...)` on full chunk:
- Use `ReadOnlySpan<byte>` slice.
- Search for next `\n` using `span.IndexOf((byte)'\n')`.
- Decode each individual line span: `Encoding.UTF8.GetString(lineSpan)`.
- Strip trailing `\r` if present.

### 2. Configuration & Models: `AppSettings` and `ILogSourceFactory`

#### `AppSettings` Update
Add configuration knobs with sensible defaults:

```csharp
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

#### `ILogSourceFactory` & `LogSourceFactory` Update
Pass `tailLineLimit`, `initialWindowBytes`, and `maxWindowBytes` to `FileTailSource`:

```csharp
public interface ILogSourceFactory
{
    ILogSource CreateFileSource(
        string filePath,
        int tailLineLimit = 50_000,
        int initialWindowBytes = 8 * 1024 * 1024,
        int maxWindowBytes = 64 * 1024 * 1024);
}
```

### 3. UI Layer: `MainWindowViewModel` Capacity & Eviction Fix

#### `FlushPendingHistorical` Fix

Ensure `tab.LogEvents` strictly adheres to `_tailLineLimit`:

```csharp
private void FlushPendingHistorical(List<EnrichedLogEvent> snapshot)
{
    var tab = SelectedTab;
    if (tab is null) return;

    tab.Status = "tailing";

    // If incoming snapshot exceeds capacity by itself, slice only the latest items
    IReadOnlyList<EnrichedLogEvent> itemsToAdd = snapshot;
    if (itemsToAdd.Count > _tailLineLimit)
    {
        itemsToAdd = snapshot.Slice(snapshot.Count - _tailLineLimit, _tailLineLimit);
    }

    var excess = (tab.LogEvents.Count + itemsToAdd.Count) - _tailLineLimit;
    if (excess > 0)
    {
        tab.EvictFromFront(excess);
    }

    foreach (var item in itemsToAdd)
    {
        _buffer.Add(item);
    }

    tab.AddLogEvents(itemsToAdd);
    RecordLinesForRate(tab, itemsToAdd.Count);

    lock (_historicalLock)
    {
        _pendingHistoricalEvents.Clear();
        _historicalFlushed = true;
    }
}
```

#### `OnNewEventsBatch` Alignment

Align batch eviction to check against `_tailLineLimit` rather than unbounded buffer growth.

### 4. Memory Bounds Comparison

| Scenario | Old Behavior | New Behavior |
|---|---|---|
| **10 MB File (~100k lines)** | Reads 100k lines, buffers all 100k in UI (~35 MB), renders 100k. | Reads 8 MB window from end, emits last 50k lines (~15 MB), renders 50k. |
| **500 MB File (~5M lines)** | Reads 500 MB, buffers 5M lines in UI (~1.7 GB), GC spike, renders 5M. | Reads 8 MB window from end (~80k lines), emits last 50k lines (~15 MB), renders 50k. Zero memory ballooning. |
| **10 GB File** | Out of Memory (OOM) crash. | Reads 8 MB from end, renders last 50k lines in ~15 MB, tail continues live. |

## Verification & Testing Plan

### Automated Core Unit Tests (`tests/LogTail.Core.Tests/Sources/FileTailSourceTests.cs`)
1. `StartAsync_WhenFileLargerThanLineLimit_EmitsOnlyTailLines`: File with 1,000 lines and limit of 100 emits exactly 100 trailing lines with `IsHistorical: true`.
2. `StartAsync_WhenFileSmallerThanWindow_EmitsAllLines`: File with 10 lines emits all 10 lines.
3. `StartAsync_WhenLinesExceedInitialWindow_ExpandsWindowUpToMax`: File with long lines where initial 1KB window only has 5 lines, expands window and fetches required line limit.
4. `StartAsync_AfterInitialTailLoad_NewAppendsStreamLive`: Verifies offset is at EOF after tail load; subsequent appends arrive with `IsHistorical: false`.
5. `StartAsync_WhenFirstLineInWindowIsPartial_DiscardsPartialAndEmitsCompleteLines`: Verifies leading partial line across window boundary is cleanly dropped.

### Automated UI Unit Tests (`tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`)
1. `StartTailingAsync_WithMoreHistoricalLinesThanLimit_RendersCappedToTailLineLimit`: Verify `tab.LogEvents.Count <= TailLineLimit`.
2. `StartTailingAsync_WhenAppendedAfterInitialLoad_EvictsFromFrontAndMaintainsCap`: Verify live appends keep collection at or below `TailLineLimit`.
3. Update existing regression test `StartTailingAsync_WithVeryManyHistoricalLines_RendersAll` to assert capped rendering instead of unbounded growth.

### Build & Hygiene Verification
1. Run `dotnet test` across the entire solution.
2. Run project hygiene check (`git grep -i -E "[A-Za-z]:[\\/]+Users|/Users/|/home/"`).
