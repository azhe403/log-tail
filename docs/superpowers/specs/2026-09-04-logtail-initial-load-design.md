# Initial Load + Tail Mode Design

**Date:** 2026-09-04
**Status:** Implemented
**Scope:** Core (`LogTail.Core`) + UI (`LogTail.UI`)

## Problem

`FileTailSource` currently starts in `StartAsync` with a misleading comment "Open file from end (tail mode)" but the code actually always starts from byte 0 (`_offset = 0`). For large log files (100k+ lines), a UI that subscribes directly to `Events` receives all historical lines as a stream — this causes UI freeze/stutter because the DataGrid/terminal renderer must handle many event updates in real time.

## Goal

Clearly separate two phases in `FileTailSource`:

1. **Initial Load** — read the entire historical file content (from byte 0 to EOF) and emit it as `RawLogEvent` with `IsHistorical = true`.
2. **Tail Mode** — read new lines incrementally as they are written after the initial load finishes. Emit them as `RawLogEvent` with `IsHistorical = false`.

The UI is signaled via the `InitialLogLoaded` event after phase 1 completes. The UI renders nothing during phase 1; after `InitialLogLoaded` is received, the UI flushes all buffered historical events and starts rendering live events.

## Design

### Core Layer

#### `ILogSource` (interface)

Add two new members:

```csharp
public interface ILogSource : IAsyncDisposable
{
    // ... existing members

    /// <summary>
    /// Raised once after all historical lines have been emitted to <see cref="Events"/>.
    /// Not raised again on file rotation; only on first successful attach.
    /// </summary>
    event Action? InitialLogLoaded;

    /// <summary>
    /// Size of the tailed file in bytes at the time of attachment.
    /// </summary>
    long FileSize { get; }
}
```

#### `FileTailSource` (implementation)

**`StartAsync` refactor:**

1. Open the file via `FileStream` (existing).
2. Set `_offset = 0`.
3. Record `_fileSizeAtStart = new FileInfo(_filePath).Length`.
4. Run `ReadLoopAsync` in a background task.

**`ReadLoopAsync` refactor — two explicit phases:**

- **Phase 1 — Initial Load**: read a 64KB chunk from `_offset = 0` and emit it as an event with `IsHistorical = true`. Loop until `currentLength == _offset`. When EOF is reached for the first time, set `_initialLogLoadedRaised = true` and call `_initialLogLoaded?.Invoke()`.
- **Phase 2 — Tail**: continue the loop with the early-skip `if (_offset >= currentLength) continue;` (existing). Events are emitted with `IsHistorical = false` (default).
- Guard: the `_initialLogLoadedRaised` flag prevents the event from being invoked twice.

**`ReopenFileAsync` — on rotation:**

- Reset to offset 0 (existing) — the new file is read from the beginning.
- **Do not** call `InitialLogLoaded` again. That is only for first-time attach.
- Add a comment: rotation resets offset but UI stays in tail mode.

**`StopAsync` / `DisposeAsync`:**

- Reset the `_initialLogLoaded` invocation list (set it to null) to avoid an event handler leak.
- Reset `_initialLogLoadedRaised = false` so that the instance can be reused.

**Event emission changes:**

- Every `_events.OnNext(new RawLogEvent(...))` in Phase 1 sets `IsHistorical: true`.
- Every `_events.OnNext(new RawLogEvent(...))` in Phase 2 (existing tail) sets `IsHistorical: false` (default value, no need to set it explicitly).
- Partial lines at the end of the historical file: also emitted (already existing behavior).

**Error handling:**

- File does not exist at `StartAsync`: still call `InitialLogLoaded` after a short timeout (1 second) so that the UI is not stuck on "Loading…". The tail loop keeps retrying via `ReopenFileAsync`.
- Read error during initial load: log the error and call `InitialLogLoaded` after the partial events have been emitted. The UI displays the number of lines loaded successfully.

#### Naming consistency

- Private fields: `_initialLogLoaded` (event delegate), `_initialLogLoadedRaised` (bool flag).
- Remove the misleading comment `// Open file from end (tail mode)` in `StartAsync`.

### UI Layer

#### `MainWindowViewModel` — changes in `StartTailingAsync`

**Buffering strategy:**

The UI subscribes to `source.Events` from the start, BUT all historical events are buffered in `List<EnrichedLogEvent> _pendingHistoricalEvents` (a new private field). Tail events (IsHistorical = false) are rendered directly without buffering.

```csharp
private readonly List<EnrichedLogEvent> _pendingHistoricalEvents = new();

// In StartTailingAsync:
_eventsSubscription = source.Events
    .Select(raw => Enrich.Transform(raw))
    .Buffer(TimeSpan.FromMilliseconds(50), 200)
    .Where(batch => batch.Count > 0)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(onNext: OnNewEventsBatch, ...);

source.InitialLogLoaded += OnInitialLogLoaded;
```

**`OnInitialLogLoaded` handler:**

```csharp
private void OnInitialLogLoaded()
{
    // Flush buffered historical events to the active tab.
    var tab = SelectedTab;
    if (tab is null || _pendingHistoricalEvents.Count == 0)
    {
        return;
    }

    int excess = (tab.LogEvents.Count + _pendingHistoricalEvents.Count) - _buffer.Capacity;
    // ... same eviction logic as OnNewEventsBatch

    foreach (var item in _pendingHistoricalEvents)
    {
        _buffer.Add(item);
        tab.AddLogEvent(item);
    }

    _pendingHistoricalEvents.Clear();
}
```

**`OnNewEventsBatch` modification:**

Check the `IsHistorical` field of the raw event (before enrichment) — or, more simply, check it via a flag on `EnrichedLogEvent`/`RawLogEvent` in the handler.

Approach: separate the historical and live subscriptions using `GroupByUntil`:

```csharp
_eventsSubscription = source.Events
    .Select(raw => (raw, enriched: Enrich.Transform(raw)))
    .Publish(events => events
        .GroupByUntil(t => t.raw.IsHistorical, g => 
            g.Key ? Observable.Empty<Unit>() : Observable.Never<Unit>())
        // historical: buffered; live: rendered directly
    );
```

Alternative approach (simpler, less Rx gymnastics): keep a single subscription, and in `OnNewEventsBatch` check `IsHistorical` on the raw event (by storing the raw reference in the enriched item, or by using the signature `Select(raw => (raw, enriched))`):

```csharp
_eventsSubscription = source.Events
    .Select(raw => (Raw: raw, Enriched: Enrich.Transform(raw)))
    .Buffer(TimeSpan.FromMilliseconds(50), 200)
    .Where(batch => batch.Count > 0)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(onNext: batch =>
    {
        // Separate historical from live based on first item
        bool isHistoricalBatch = batch[0].Raw.IsHistorical;
        if (isHistoricalBatch)
        {
            // Buffer; will be flushed by OnInitialLogLoaded
            foreach (var (_, enriched) in batch)
            {
                _pendingHistoricalEvents.Add(enriched);
            }
        }
        else
        {
            // Live: render directly
            OnNewEventsBatch(batch.Select(b => b.Enriched).ToList());
        }
    });
```

**Cleanup in `StopCurrentSourceAsync`:**

- Unsubscribe from `source.InitialLogLoaded` (`source.InitialLogLoaded -= OnInitialLogLoaded`).
- Clear `_pendingHistoricalEvents`.
- Dispose the old subscription.

**Subscription ordering — important for correctness:**

In `StartTailingAsync`, the order of operations MUST be:

1. `source.InitialLogLoaded += OnInitialLogLoaded` (subscribe to the event BEFORE the source starts)
2. `_eventsSubscription = source.Events...Subscribe(...)` (subscribe to the events)
3. `await source.StartAsync(...)` (start the source — it may call InitialLogLoaded immediately)

This order guarantees that there is no race condition where InitialLogLoaded is called before the handler is attached.

**Batch rendering for the flush:**

`OnInitialLogLoaded` must not `Add` items one by one to `LogEvents` (ObservableCollection.Add triggers CollectionChanged per item — UI freeze). Use the same pattern as `OnNewEventsBatch`: add everything to a list, then assign via `tab.LogEvents.AddRange` (or a manual loop with a batch update). If there is no `AddRange` extension, create a local helper.

#### `TabViewModel`

No changes — `LogEvents` (ObservableCollection) is already the render target. Adding historical lines via `AddLogEvent` (existing) triggers normal UI updates.

#### Status display

`TabViewModel.Status` is initially `"loading"` and is then set to `"tailing"` after `OnInitialLogLoaded` is called. This is visible in the `BuildTabStatus` output.

### Testing

#### Core tests (`LogTail.Core.Tests/Sources/FileTailSourceTests.cs`)

New tests:

1. `StartAsync_WhenFileHasHistoricalLines_EmitsThemWithIsHistoricalTrue`
   - File with 5 lines, start tail, verify that the first 5 events are all `IsHistorical == true`.

2. `StartAsync_AfterInitialLoad_RaisesInitialLogLoadedEventOnce`
   - File with 3 lines, start tail, wait for the event, verify that `InitialLogLoaded` is called once (1x).

3. `StartAsync_AfterInitialLoad_AppendedLinesHaveIsHistoricalFalse`
   - File with 2 lines, start, wait for the initial load to complete, append 1 line, verify that the new event has `IsHistorical == false`.

4. `StartAsync_WhenFileRotated_DoesNotRaiseInitialLogLoadedAgain`
   - File with 1 line, start, wait for the initial load, rotate the file, append a line, verify that `InitialLogLoaded` is still 1x (does not increase again).

5. `StartAsync_WhenFileEmpty_ImmediatelyRaisesInitialLogLoaded`
   - Empty file, start, verify that `InitialLogLoaded` is called immediately, with 0 events.

6. `StartAsync_WhenFileDoesNotExist_StillRaisesInitialLogLoadedAfterTimeout`
   - Path does not exist, start, wait, verify that `InitialLogLoaded` is called.

7. `FileSize_AfterStart_ReturnsCorrectSize`
   - 1KB file, start, verify that `FileSize == 1024`.

8. `StopAsync_WhenCalled_AllowsReuseOfSource`
   - Start, stop, start again, verify that `InitialLogLoaded` is called twice (2x), once per start.

#### UI tests (`LogTail.UI.Tests/MainWindowViewModelTests.cs`)

New tests:

1. `StartTailingAsync_DuringInitialLoad_DoesNotRenderToTab`
   - File with 100 lines, start tail, wait briefly (InitialLogLoaded may not have triggered yet), verify that `SelectedTab.LogEvents.Count == 0`.

2. `StartTailingAsync_AfterInitialLogLoaded_RendersAllHistoricalEvents`
   - File with 50 lines, start tail, wait for InitialLogLoaded, verify that `SelectedTab.LogEvents.Count == 50`.

3. `StartTailingAsync_AfterInitialLoad_AppendedLinesRenderLive`
   - File with 10 lines, start, wait for InitialLogLoaded, append 1 line, verify that `LogEvents.Count == 11`.

4. `StartTailingAsync_HistoricalEvents_BypassBuffering`
   - Verify that historical events are not rendered twice (not through the OnNewEventsBatch path that buffers for 50ms).

## Out of Scope

- Progress bar / percentage UI during the initial load
- User-facing cancellation token to cancel a slow initial load
- Async initial load that can be paused

## Risks

1. **Memory usage**: for a file with 100k+ lines, all historical events are buffered in a `List<EnrichedLogEvent>` in the UI. Worst case ~100k * ~200 bytes = ~20MB. Acceptable for now; it can be mitigated later with chunked rendering.

2. **UI freeze on flush**: after `InitialLogLoaded` is received, the UI flushes 100k events at once. ObservableCollection.Add per item triggers CollectionChanged per item → potential freeze. Mitigation: add a batch Add to `LogEvents` (the existing `OnNewEventsBatch` already handles this via a List batch; just replicate that pattern).

3. **Race condition**: the `source.InitialLogLoaded` event can be called before the UI subscription is ready. Mitigation: `StartTailingAsync` subscribes to the event BEFORE the source's `StartAsync` is called (existing — already correct).

## Implementation Order

1. Core: add the `IsHistorical` flag to `FileTailSource` event emission
2. Core: add the `InitialLogLoaded` event + `FileSize` property to `ILogSource`/`FileTailSource`
3. Core: fix the misleading comment + cleanup logic
4. Core: add unit tests
5. UI: refactor `StartTailingAsync` to buffer historical events
6. UI: add an `OnInitialLogLoaded` handler to flush the buffer
7. UI: add tests
8. Run full test suite + manual smoke test
