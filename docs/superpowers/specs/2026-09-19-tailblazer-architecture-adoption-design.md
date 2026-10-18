# Technical Design Specification: Adopting TailBlazer Reactive & Virtualized Architecture

- **Date:** 2026-09-19
- **Status:** Abandoned (not implemented)
- **Target Project:** LogTail (`LogTail.Core`, `LogTail.UI`)
- **Reference Architecture:** [RolandPheasant/TailBlazer](https://github.com/RolandPheasant/TailBlazer)

---

## 1. Executive Summary & Problem Statement

### 1.1 Context
LogTail is a cross-platform desktop log monitoring tool built with .NET 10, ReactiveUI, and Avalonia. Currently, it streams log lines into a custom in-memory `RingBuffer<EnrichedLogEvent>` (bounded between `BufferCapacity` of 50,000 and `MaxBufferCapacity` of 2,000,000 lines) and an observable collection (`BulkObservableCollection<EnrichedLogEvent>`) bound to an Avalonia `ItemsControl`.

### 1.2 Observed Bugs & Architectural Bottlenecks
1. **Line Number Freeze on Eviction:**
   In `TabViewModel.cs`, line numbers are assigned using:
   ```csharp
   var nextNumber = LineCount + 1; // where LineCount = LogEvents.Count
   ```
   When the buffer hits `BufferCapacity` / `MaxBufferCapacity`, `MainWindowViewModel.OnNewEventsBatch` executes `tab.EvictFromFront(excess)` before adding new lines. `EvictFromFront` sets `LineCount = LogEvents.Count`, reducing the count. The next batch assigns `LineCount + 1`, preventing line numbers from incrementing past the buffer limit and causing numbers to freeze or repeat even though new lines continue to stream in.
2. **High Memory Overhead (RAM Bounded by String Objects):**
   Storing 2,000,000 `EnrichedLogEvent` instances (raw line string, parsed timestamp, log level, color key) in memory consumes hundreds of megabytes to gigabytes of RAM. Extremely large files (10GB - 50GB) cannot be completely loaded or tailed indefinitely without risking Out-Of-Memory (OOM) exceptions.
3. **UI Virtualization Limits:**
   Even with UI item virtualization, holding up to 2 million rich objects in an observable collection degrades UI responsiveness, search performance, and GC pause times.

---

## 2. Solution: TailBlazer Architecture Adaptation

Roland Pheasant's **TailBlazer** successfully solves these challenges by combining:
1. **Monotonic Line Identity:** Total lines streamed/read are decoupled from UI presentation windows.
2. **Dynamic Data Reactive Pipeline:** Utilizing `DynamicData`'s `SourceCache` / `SourceList` with reactive operators (`.Buffer()`, `.Virtualise()`, `.Batch()`).
3. **Sparse Byte-Offset File Indexing (`LineInfo` & `FileSegmenter`):** Storing only 64-bit byte offsets and line lengths in RAM, reading raw text from disk on-demand as the user scrolls into the viewport.

---

## 3. Phased Implementation Roadmap

### Phase 1: Monotonic Line Counter & Resilient Eviction (Immediate Fix)
*Goal: Fix line numbering freeze and decouple line metadata from UI collection size without breaking existing APIs.*

1. **State Isolation in `TabViewModel`:**
   - Introduce an explicit `long _totalLinesAppended` counter.
   - Increment `_totalLinesAppended` monotonically on every added line.
   - Decouple `EnrichedLogEvent.LineNumber` from `LogEvents.Count`.
   - `EvictFromFront(int count)` must only trim the collection and update `LineCount`, never resetting or altering `_totalLinesAppended`.
2. **Clean Reset Handling:**
   - `ClearEvents()` resets both `LogEvents` and `_totalLinesAppended` to 0.

---

### Phase 2: Reactive Dynamic Data Pipeline (Modern Rx.NET)
*Goal: High-throughput, non-blocking stream processing with thread-safe delta propagation.*

1. **DynamicData Core Adoption:**
   - Introduce `DynamicData` (`SourceList<EnrichedLogEvent>` or `SourceCache<EnrichedLogEvent, long>`).
   - Replace manual `EvictFromFront` logic with DynamicData's `.LimitSizeTo(capacity)`.
2. **Adaptive Batching & Throttling:**
   - Batch incoming raw file chunks using:
     ```csharp
     source.Events
         .Buffer(TimeSpan.FromMilliseconds(50), 2000)
         .Where(batch => batch.Count > 0)
         .ObserveOn(RxApp.TaskpoolScheduler)
         .Select(batch => EnrichBatch(batch))
         .ObserveOn(RxApp.MainThreadScheduler)
         .Bind(out _logEvents)
         .Subscribe();
     ```
   - Eliminates UI thread freezing during high log ingestion rates (>50,000 lines/second).

---

### Phase 3: Zero-OOM Sparse File Indexing & Viewport Virtualization (Future Scale)
*Goal: Support 50GB+ log files with negligible RAM footprint by copying TailBlazer's disk-backed indexing pattern.*

1. **`LineIndex` / `LineInfo` Struct:**
   ```csharp
   public readonly record struct LineInfo(
       long StartOffset,
       int Length,
       long LineNumber,
       LogLevel Level,
       DateTimeOffset? Timestamp);
   ```
   Memory footprint: ~32 bytes per line (instead of ~200+ bytes for full string object graph). 1 million lines indexed in memory requires only ~32 MB RAM.
2. **On-Demand Text Retrieval (`VirtualFileScroller`):**
   - The UI viewport (e.g. 50-100 visible lines in Avalonia List/DataGrid) requests only the visible slice: `[startIndex, count]`.
   - A dedicated random-access file reader seeks directly to `StartOffset`, reads `Length` bytes, decodes the text, and populates the visible item controls.

---

## 4. Technical Specifications & API Changes

### 4.1 Phase 1 Detailed Design: `TabViewModel`

```csharp
public sealed class TabViewModel : ReactiveObject
{
    private long _totalLinesAppended;
    public long TotalLinesAppended => _totalLinesAppended;

    public void AddLogEvents(IList<EnrichedLogEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;

        var numbered = new List<EnrichedLogEvent>(batch.Count);
        foreach (var item in batch)
        {
            _totalLinesAppended++;
            numbered.Add(TrimLineIfNeeded(item) with { LineNumber = (int)_totalLinesAppended });
        }

        LogEvents.AddRange(numbered);
        LineCount = LogEvents.Count;
    }

    public void EvictFromFront(int count)
    {
        if (count <= 0) return;
        LogEvents.RemoveFromFront(count);
        LineCount = LogEvents.Count;
    }

    public void ClearEvents()
    {
        LogEvents.Clear();
        LineCount = 0;
        _totalLinesAppended = 0;
    }
}
```

### 4.2 Handling Initial Tail Load Window
When tailing large existing files, `FileTailSource` loads the tail window (default 50,000 lines).
- If the file was windowed and starts from line `K`, `FileTailSource` should provide the initial base line offset `baseLineNumber` (if known) or start counting from 1 for the session view.
- Successive live-tail lines continue strictly from `_totalLinesAppended + 1`.

---

## 5. Verification & Testing Strategy

### 5.1 Unit Tests (`TabViewModelTests.cs`)
1. **`AddLogEvents_WhenEvictionOccurs_MaintainsMonotonicLineNumbers`:**
   - Add 5 items (line numbers 1..5).
   - Evict 3 items (remaining count = 2).
   - Add 3 items.
   - Verify new items receive line numbers 6, 7, 8 (NOT 3, 4, 5).
2. **`ClearEvents_ResetsLineNumbers`:**
   - Add items, call `ClearEvents()`, add new items.
   - Verify line numbers restart at 1.

### 5.2 Performance & Memory Benchmarks
- Stream 100,000 events into a buffer capped at 10,000.
- Verify memory consumption remains flat (O(1) relative to total streamed lines).
- Verify the last visible item has `LineNumber == 100000`.

---

## 6. Next Steps & Execution Strategy

1. **Review & Approval of this Design Spec.**
2. **Implementation of Phase 1** (Immediate fix for monotonic line numbers & eviction behavior).
3. **Creation of Phase 2 Task Plan** (Evaluating `DynamicData` package integration into `LogTail.Core`).
