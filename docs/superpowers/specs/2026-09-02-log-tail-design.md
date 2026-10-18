# Log Tail — Design Spec (Milestone 1: Foundation)

Date: 2026-09-02
Status: Implemented
Scope: Milestone 1 only (foundation: file tail, ring buffer, theme persistence). Milestones 2–5 designed and brainstormed separately.

## 1. Purpose

`log-tail` is a desktop application that tails log files in real time on Windows, macOS, and Linux, built on Avalonia 11 + ReactiveUI. Milestone 1 delivers the smallest end-to-end useful slice: open a file, watch new lines appear, persist the user's theme preference. Subsequent milestones add filters, multiple source types, and richer interactivity.

## 2. Platform & Stack

- **Target frameworks**: `net10.0` (Windows/macOS/Linux desktop). No browser, no mobile.
- **UI framework**: Avalonia 11.3 or newer, Fluent theme.
- **MVVM framework**: ReactiveUI (chosen for natural fit with streaming, observable-based UI state).
- **Testing**: xUnit + FluentAssertions (Core); xUnit + Avalonia.Headless (UI).
- **SDK**: .NET 10 SDK (verified present at `C:\Program Files\dotnet\sdk\10.0.400`).

## 3. Solution Layout

Multi-project solution, chosen to keep Core testable without Avalonia bootstrap and to enable clean unit tests on the pipeline.

```
LogTail.sln
src/
  LogTail.Core/              (class lib, net10.0, no Avalonia deps)
    Sources/                 ILogSource, ILogSourceFactory, FileTailSource
    Pipeline/                LevelDetector, TimestampParser, Filter, Highlight (M2/M3 — stubbed in M1)
    Buffer/                  RingBuffer<T>
    Persistence/             SettingsStore
    Models/                  RawLogEvent, EnrichedLogEvent, LogLevel, ThemeMode, AppSettings
  LogTail.UI/                (Avalonia app, net10.0)
    App.axaml(.cs)
    Program.cs
    ViewModels/              MainWindowViewModel
    Views/                   MainWindow.axaml(.cs)
    Converters/              (none in M1)
tests/
  LogTail.Core.Tests/        (xUnit + FluentAssertions)
  LogTail.UI.Tests/          (xUnit + Avalonia.Headless)
```

## 4. Data Model

```csharp
namespace LogTail.Core.Models;

public readonly record struct RawLogEvent(
    DateTimeOffset ReadAt,
    string SourceId,
    long FileOffset,
    string Line,
    bool IsHistorical);

public sealed record EnrichedLogEvent(
    RawLogEvent Raw,
    LogLevel Level,
    DateTimeOffset? Timestamp,
    string? LevelColorKey,
    bool IsHighlighted,
    bool IsHidden);

public enum LogLevel
{
    Unknown,
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public sealed record AppSettings(
    ThemeMode Theme,
    int BufferCapacity,
    TimeSpan PollInterval,
    string DefaultEncoding);
```

For Milestone 1, `EnrichedLogEvent.Level` is always `Unknown`, `Timestamp` is `null`, `IsHighlighted`/`IsHidden` are `false`. Pipeline operators are injected but no-op; wiring is in place for Milestone 2.

## 5. Ring Buffer

```csharp
namespace LogTail.Core.Buffer;

public sealed class RingBuffer<T> : IReadOnlyList<T>
{
    public RingBuffer(int capacity);

    public int Count { get; }
    public int Capacity { get; }

    public void Add(T item);
    public void Clear();

    public T this[int index] { get; }    // 0 = oldest, Count-1 = newest
}
```

Thread-safe (lock-based; the hot path is dominated by UI events, not high concurrency). Eviction drops the oldest item when at capacity.

## 6. ILogSource & FileTailSource (Milestone 1)

```csharp
namespace LogTail.Core.Sources;

public interface ILogSource : IAsyncDisposable
{
    string DisplayName { get; }
    IObservable<RawLogEvent> Events { get; }
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct);
    ValueTask StopAsync();
}

public interface ILogSourceFactory
{
    ILogSource CreateFileSource(string filePath);
    // CreateFolderSource, CreateStdinSource land in M3.
}

public sealed class FileTailSource : ILogSource
{
    public FileTailSource(string filePath, TimeSpan pollInterval = default);
    // Default pollInterval = 250ms.
}
```

Behavior of `FileTailSource.StartAsync`:

1. Open the file with `FileStream` and `FileShare.ReadWrite | FileShare.Delete` (mandatory — without `ReadWrite | Delete` the app locks the file and the producer cannot append).
2. Seek to end of file (default `tail` behavior). Reading from offset 0 is a future option.
3. Create a `FileSystemWatcher` on the file's parent directory, filtered to the file name, watching `LastWrite | Size | Rename | Delete`.
4. Start a `Timer` with `pollInterval` as the safety-net poller.
5. Begin the read loop until cancellation: read new bytes from `_offset`, decode UTF-8 (with BOM detection), split on `\n` (handle `\r\n` and partial last line).
6. On each complete line, emit `RawLogEvent` via the subject.
7. On watcher `Deleted`/`Renamed` event OR poll detects `_offset > newLength`, treat as rotation/truncation: in Milestone 1 log and update status; rotation handling UI lands in M3.

Thread safety: event emission via `Subject<RawLogEvent>` (Rx thread-safe). Read loop runs on a dedicated background task.

## 7. Persistence

```csharp
namespace LogTail.Core.Persistence;

public sealed class SettingsStore
{
    public SettingsStore(string appDataDirectory);

    public AppSettings Load();
    public void Save(AppSettings settings);
    public void Update(Func<AppSettings, AppSettings> mutate);
}
```

JSON at `%APPDATA%/log-tail/settings.json` on Windows, `~/.config/log-tail/settings.json` on Linux, `~/Library/Application Support/log-tail/settings.json` on macOS (resolved via `Environment.SpecialFolder.LocalApplicationData` with cross-platform fallback). On corrupt JSON, log and fall back to defaults.

Milestone 1 only persists `Theme`. `BufferCapacity`, `PollInterval`, `DefaultEncoding` exist in the model for M4 settings window, but defaults are used.

## 8. ViewModel

```csharp
namespace LogTail.UI.ViewModels;

public sealed class MainWindowViewModel : ReactiveObject
{
    [Reactive]
    public string WindowTitle { get; set; } = "Log Tail";

    [Reactive]
    public string? CurrentFilePath { get; set; }

    [Reactive]
    public string StatusMessage { get; set; } = "No file open";

    [Reactive]
    public ThemeMode CurrentTheme { get; set; }

    public ObservableCollection<EnrichedLogEvent> VisibleEvents { get; } = new();

    public ReactiveCommand<Unit, Unit> OpenFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<ThemeMode, Unit> SetThemeCommand { get; }

    [ObservableAsProperty]
    public bool IsTailing { get; }

    public bool CanOpenFile => !IsTailing;
}
```

Constructor takes `SettingsStore` and `ILogSourceFactory` (constructor injection; wiring in `App.axaml.cs`). On construction, `CurrentTheme` is loaded from settings.

`OpenFileCommand` opens a file via `IStorageProvider.OpenFilePickerAsync`, creates a `FileTailSource`, subscribes to `Events`, and pushes items through `Enrich` (no-op in M1) → ring buffer → `VisibleEvents` (ObservableCollection bound to `ListBox`). On event arrival, if the buffer dropped old items, clear `VisibleEvents` and refill from buffer to keep UI consistent.

`SetThemeCommand` updates `CurrentTheme`, applies to `Application.Current.RequestedThemeVariant`, and persists via `SettingsStore.Update`.

## 9. UI (Avalonia XAML)

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:LogTail.UI.ViewModels"
        x:Class="LogTail.UI.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="{Binding WindowTitle}">
  <DockPanel>
    <Menu DockPanel.Dock="Top">
      <MenuItem Header="_File">
        <MenuItem Header="_Open..." Command="{Binding OpenFileCommand}" />
        <Separator />
        <MenuItem Header="E_xit" />
      </MenuItem>
      <MenuItem Header="_View">
        <MenuItem Header="_Theme">
          <MenuItem Header="System" Command="{Binding SetThemeCommand}" CommandParameter="System" />
          <MenuItem Header="Light" Command="{Binding SetThemeCommand}" CommandParameter="Light" />
          <MenuItem Header="Dark" Command="{Binding SetThemeCommand}" CommandParameter="Dark" />
        </MenuItem>
      </MenuItem>
    </Menu>

    <Grid DockPanel.Dock="Bottom" Height="24" ColumnDefinitions="*">
      <TextBlock Text="{Binding StatusMessage}" Margin="6,0" VerticalAlignment="Center" />
    </Grid>

    <ListBox ItemsSource="{Binding VisibleEvents}"
             VirtualizingStackPanel.IsVirtualized="True"
             VirtualizingStackPanel.VirtualizationMode="Recycling">
      <ListBox.ItemTemplate>
        <DataTemplate DataType="models:EnrichedLogEvent">
          <TextBlock Text="{Binding Raw.Line}"
                     FontFamily="Consolas,Menlo,DejaVu Sans Mono,monospace" />
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</Window>
```

## 10. Code Style (Conventions)

- **Attributes on their own line, blank line before the member**:

  ```csharp
  [Reactive]
  public string WindowTitle { get; set; } = "Log Tail";
  ```

- **Indentation**: 4 spaces.
- **Braces**: Allman for type/method declarations (brace on its own line); K&R for control flow (`if`, `for`, `try`, etc.).
- **Namespaces**: file-scoped (`namespace Foo;`).
- **`var`**: preferred when the type is obvious from the RHS; explicit type when ambiguous or for public API surface.
- **Private fields**: `_camelCase`.
- **Records / record structs**: `public readonly record struct` for value-like events, `public sealed record` for class-like DTOs.

## 11. Error Handling

- **File open errors** (not found, permission denied, locked): `StatusMessage` updated, no crash. `OpenFileCommand` swallows via ReactiveUI default `ThrownExceptions` (logged to debug output).
- **File removed while tailing**: watcher `Deleted`/`Renamed` event disposes current source, status updates. Reconnect UI lands in M3.
- **Watcher internal exception**: logged to `ILogTailLogger` (a small internal interface with a default `ConsoleLogger` implementation; defined in `LogTail.Core.Persistence` or a new `LogTail.Core.Logging` folder). Status bar updates. This avoids pulling in `Microsoft.Extensions.Logging` for Milestone 1; the abstraction allows a future swap to MEL or Serilog without changing call sites.
- **No global try-catch**. Exceptions bubble to ReactiveUI `ThrownExceptions` and are logged. App does not auto-restart on error; user clicks Open again.

## 12. Testing Strategy

### `LogTail.Core.Tests` (xUnit + FluentAssertions)

- `RingBufferTests`:
  - Add up to capacity, eviction drops oldest.
  - Indexer returns correct order.
  - `Clear` resets state.
  - Concurrent `Add` does not corrupt (parallel test).
- `SettingsStoreTests`:
  - Round-trip JSON to temp file.
  - Default values when file missing.
  - Corrupt file falls back to defaults and logs.
- `FileTailSourceTests`:
  - Tail append-only file: events emitted in order.
  - Truncation: when file shrinks below `_offset`, reset offset and continue.
  - Rotation: when file renamed, synthetic event emitted (M1 logs; M3 wires it up).
  - File sharing: opening the file does not block a concurrent writer.
- `EnrichTests` (M1 no-op, but pipeline call shape is tested):
  - Always returns `EnrichedLogEvent` with `Level = Unknown`, `Timestamp = null`, `IsHighlighted = false`, `IsHidden = false`.

### `LogTail.UI.Tests` (xUnit + Avalonia.Headless)

- `MainWindowViewModelTests`:
  - `OpenFileCommand` happy path: events emitted, `VisibleEvents` populated.
  - Theme persistence: changing `CurrentTheme` and reconstructing the VM loads the same theme.
  - `ClearCommand` empties `VisibleEvents`.
- `ThemeApplicationTests`:
  - Changing `CurrentTheme` updates `Application.Current.RequestedThemeVariant`.

## 13. Definition of Done (Milestone 1)

1. User can open a file via **File → Open...** and see new lines appear in real time (latency < 500 ms from disk write to UI render via `FileSystemWatcher` + 250 ms poll fallback).
2. Ring buffer caps at 50,000 lines; eviction is automatic and scroll remains smooth under sustained write pressure.
3. Theme (System / Light / Dark) is persisted across app restarts.
4. Status bar shows current state (file path, line count buffered, last error).
5. All Core and UI tests pass via `dotnet test`.
6. App runs unchanged on Windows, macOS, and Linux via `dotnet run --project src/LogTail.UI`.

## 14. Out of Scope (Milestone 1)

The following are explicitly **not** part of Milestone 1 and will be designed in separate brainstorming sessions:

- Folder watcher, stdin/pipe source, rotated historical files (`.log.N`, `.gz`) — **M3**.
- Substring/regex filter, highlight mode, level coloring, timestamp parsing — **M2**.
- Recent files list, filter pattern persistence, advanced settings window — **M4**.
- Click-to-expand line detail, right-click context menu, jump-to-time — **M4**.
- Open from beginning of file (vs. tail from end) — **M4**.

## 15. Risks & Open Questions

- **Avalonia 11 + .NET 10 compatibility**: Avalonia 11.3 supports .NET 8/9. .NET 10 support may need the latest 11.x at time of implementation. Verify before scaffolding.
- **ReactiveUI Avalonia binding churn**: ReactiveUI's Avalonia package has seen breaking changes between 11.x minor versions. Pin a version after the first successful `dotnet run` and document in the README.
- **`FileSystemWatcher` reliability on Linux**: inotify-based watcher can miss events under heavy write load. The 250 ms poll fallback is the safety net; verify on the actual target Linux distribution.
- **`ObservableCollection<T>` and ring buffer drift**: when eviction happens, `VisibleEvents` must be fully repopulated from the buffer (not just appended). This is O(n) on eviction and acceptable for 50k items.