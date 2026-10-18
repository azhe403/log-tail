# Session Restore + Settings Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore all tabs from the previous session on startup (toggleable, on by default) and move Theme into a new Settings dialog opened from a top-right gear icon.

**Architecture:** Preferences stay in `settings.json` (`AppSettings` gains `RestoreLastSession`). Session state (tab list + active file) lives in a separate `session.json` via a new `SessionStore`, so writing on every tab change never races with preference writes. A small `SessionTracker` subscribes to the tabs collection and selected tab and persists on change; it also carries "unresolved" paths so files that are missing at startup are skipped but kept for next time. Theme ownership moves from `MainWindowViewModel` to a new `SettingsViewModel` that `App` subscribes to.

**Tech Stack:** .NET 10, Avalonia 11, ReactiveUI, System.Text.Json, xUnit + FluentAssertions.

## Global Constraints

- Target framework: `net10.0`. Do not change it.
- Test framework is xUnit with FluentAssertions (`result.Should()...`); follow existing test style in `tests/LogTail.Core.Tests` and `tests/LogTail.UI.Tests`.
- One type per file. Do not combine classes/interfaces in a single file.
- **Do NOT commit per task.** The user's standing rule is a single commit after ALL code is written and tests pass (see Task 8). Ignore the usual "commit each task" habit.
- Never chain shell commands with `&&`; run each command as its own step.
- Never write machine-specific absolute paths (`<drive>:\Users\...`, `/Users/...`, `/home/...`) or the user's name into tracked files. In tests use temp dirs via `Path.GetTempPath()` and portable fake paths.
- After editing any `.cs` file, if Rider MCP tools are available, run `post_edit_quality_check` via `rider_execute_tool`. Otherwise the build in the task's verification step covers it.
- Before the final commit, present a code review verdict and run the project hygiene scan (Task 8).
- Settings live in `%LocalAppData%/log-tail/` (`settings.json`, and now `session.json`).

---

### Task 1: Core model — `RestoreLastSession` + `SessionState`

**Files:**
- Modify: `src/LogTail.Core/Models/AppSettings.cs`
- Create: `src/LogTail.Core/Models/SessionState.cs`
- Test: `tests/LogTail.Core.Tests/Persistence/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AppSettings.RestoreLastSession` (`bool`, default `true`); `SessionState(IReadOnlyList<string> Files, string? ActiveFile)`.

- [ ] **Step 1: Write the failing tests**

Append these two facts inside `SettingsStoreTests` (before the closing brace):

```csharp
    [Fact]
    public void Load_WhenFileDoesNotExist_RestoreLastSessionDefaultsTrue()
    {
        var sut = new SettingsStore(_tempDir);

        sut.Load().RestoreLastSession.Should().BeTrue();
    }

    [Fact]
    public void Save_WhenRestoreDisabled_RoundtripsFalse()
    {
        var sut = new SettingsStore(_tempDir);

        sut.Save(new AppSettings(RestoreLastSession: false));

        sut.Load().RestoreLastSession.Should().BeFalse();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: FAIL — `AppSettings` has no `RestoreLastSession` (compile error).

- [ ] **Step 3: Add the field to `AppSettings`**

Replace the whole body of `src/LogTail.Core/Models/AppSettings.cs`:

```csharp
namespace LogTail.Core.Models;

public sealed record AppSettings(
    ThemeMode Theme = ThemeMode.System,
    int BufferCapacity = 50_000,
    int MaxBufferCapacity = 2_000_000,
    TimeSpan PollInterval = default,
    string DefaultEncoding = "utf-8",
    bool RestoreLastSession = true);
```

- [ ] **Step 4: Create `SessionState`**

Create `src/LogTail.Core/Models/SessionState.cs`:

```csharp
namespace LogTail.Core.Models;

public sealed record SessionState(
    IReadOnlyList<string> Files,
    string? ActiveFile);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS (all `SettingsStoreTests` green).

---

### Task 2: Core persistence — `SessionStore`

**Files:**
- Create: `src/LogTail.Core/Persistence/SessionStore.cs`
- Test: `tests/LogTail.Core.Tests/Persistence/SessionStoreTests.cs`

**Interfaces:**
- Consumes: `SessionState` (Task 1), `ILogTailLogger`, `ConsoleLogger`.
- Produces: `SessionStore(string appDataDirectory, ILogTailLogger? logger = null)` with `SessionState Load()`, `void Save(SessionState)`, `void Update(Func<SessionState, SessionState>)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogTail.Core.Tests/Persistence/SessionStoreTests.cs`:

```csharp
using FluentAssertions;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using Xunit;

namespace LogTail.Core.Tests.Persistence;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-session-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsEmpty()
    {
        var sut = new SessionStore(_tempDir);

        var result = sut.Load();

        result.Files.Should().BeEmpty();
        result.ActiveFile.Should().BeNull();
    }

    [Fact]
    public void Save_WhenSaved_RoundtripsFilesAndActive()
    {
        var sut = new SessionStore(_tempDir);
        var state = new SessionState(new[] { "a.log", "b.log" }, "b.log");

        sut.Save(state);
        var loaded = sut.Load();

        loaded.Files.Should().Equal("a.log", "b.log");
        loaded.ActiveFile.Should().Be("b.log");
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "session.json"), "{{{{not json}}}}");
        var sut = new SessionStore(_tempDir);

        var result = sut.Load();

        result.Files.Should().BeEmpty();
        result.ActiveFile.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~SessionStoreTests"`
Expected: FAIL — `SessionStore` does not exist (compile error).

- [ ] **Step 3: Implement `SessionStore`**

Create `src/LogTail.Core/Persistence/SessionStore.cs`:

```csharp
using System.Text.Json;
using LogTail.Core.Logging;
using LogTail.Core.Models;

namespace LogTail.Core.Persistence;

public sealed class SessionStore
{
    private readonly string _sessionPath;
    private readonly ILogTailLogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SessionStore(string appDataDirectory, ILogTailLogger? logger = null)
    {
        _sessionPath = Path.Combine(appDataDirectory, "session.json");
        _logger = logger ?? new ConsoleLogger();
    }

    public SessionState Load()
    {
        try
        {
            if (!File.Exists(_sessionPath))
            {
                return Empty;
            }

            var json = File.ReadAllText(_sessionPath);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            return state is null ? Empty : state;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load session, using empty: {ex.Message}");
            return Empty;
        }
    }

    public void Save(SessionState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_sessionPath)!;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_sessionPath, json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save session: {ex.Message}");
        }
    }

    public void Update(Func<SessionState, SessionState> mutate)
    {
        Save(mutate(Load()));
    }

    private static SessionState Empty => new(Array.Empty<string>(), null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.Core.Tests/LogTail.Core.Tests.csproj --filter "FullyQualifiedName~SessionStoreTests"`
Expected: PASS (3 tests green).

---

### Task 3: UI — `SettingsViewModel`

**Files:**
- Create: `src/LogTail.UI/ViewModels/SettingsViewModel.cs`
- Test: `tests/LogTail.UI.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `SettingsStore`, `AppSettings`, `ThemeMode`.
- Produces: `SettingsViewModel(SettingsStore settings, ILogTailLogger? logger = null)` with `ThemeMode Theme { get; set; }`, `bool RestoreLastSession { get; set; }`, `IReadOnlyList<ThemeMode> ThemeOptions { get; }`, `event Action<ThemeMode>? ThemeChanged`, `event Action<bool>? RestoreLastSessionChanged`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogTail.UI.Tests/ViewModels/SettingsViewModelTests.cs`:

```csharp
using FluentAssertions;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using LogTail.UI.ViewModels;
using Xunit;

namespace LogTail.UI.Tests.ViewModels;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-settings-vm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_ReadsStoredTheme()
    {
        var store = new SettingsStore(_tempDir);
        store.Save(new AppSettings(Theme: ThemeMode.Dark));

        var sut = new SettingsViewModel(store);

        sut.Theme.Should().Be(ThemeMode.Dark);
    }

    [Fact]
    public void Theme_WhenSet_PersistsAndRaisesEvent()
    {
        var store = new SettingsStore(_tempDir);
        var sut = new SettingsViewModel(store);
        ThemeMode? observed = null;
        sut.ThemeChanged += mode => observed = mode;

        sut.Theme = ThemeMode.Light;

        observed.Should().Be(ThemeMode.Light);
        store.Load().Theme.Should().Be(ThemeMode.Light);
    }

    [Fact]
    public void RestoreLastSession_WhenSet_PersistsAndRaisesEvent()
    {
        var store = new SettingsStore(_tempDir);
        var sut = new SettingsViewModel(store);
        bool? observed = null;
        sut.RestoreLastSessionChanged += enabled => observed = enabled;

        sut.RestoreLastSession = false;

        observed.Should().BeFalse();
        store.Load().RestoreLastSession.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: FAIL — `SettingsViewModel` does not exist (compile error).

- [ ] **Step 3: Implement `SettingsViewModel`**

Create `src/LogTail.UI/ViewModels/SettingsViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using ReactiveUI;

namespace LogTail.UI.ViewModels;

public sealed class SettingsViewModel : ReactiveObject
{
    private readonly SettingsStore _settings;
    private ThemeMode _theme;
    private bool _restoreLastSession;

    public SettingsViewModel(SettingsStore settings, ILogTailLogger? logger = null)
    {
        _settings = settings;
        var loaded = _settings.Load();
        _theme = loaded.Theme;
        _restoreLastSession = loaded.RestoreLastSession;
    }

    public ThemeMode Theme
    {
        get => _theme;
        set
        {
            if (_theme == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _theme, value);
            _settings.Update(s => s with { Theme = value });
            ThemeChanged?.Invoke(value);
        }
    }

    public bool RestoreLastSession
    {
        get => _restoreLastSession;
        set
        {
            if (_restoreLastSession == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _restoreLastSession, value);
            _settings.Update(s => s with { RestoreLastSession = value });
            RestoreLastSessionChanged?.Invoke(value);
        }
    }

    public IReadOnlyList<ThemeMode> ThemeOptions { get; } = new[]
    {
        ThemeMode.System,
        ThemeMode.Light,
        ThemeMode.Dark
    };

    public event Action<ThemeMode>? ThemeChanged;

    public event Action<bool>? RestoreLastSessionChanged;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"`
Expected: PASS (3 tests green).

---

### Task 4: UI — `SessionTracker`

**Files:**
- Create: `src/LogTail.UI/ViewModels/SessionTracker.cs`
- Test: `tests/LogTail.UI.Tests/ViewModels/SessionTrackerTests.cs`

**Interfaces:**
- Consumes: `SessionStore`, `SessionState`, `TabViewModel`, `ILogTailLogger`.
- Produces: `SessionTracker(SessionStore store, ObservableCollection<TabViewModel> tabs, IObservable<TabViewModel?> selectedTab, ILogTailLogger? logger = null)` with `bool IsEnabled { get; set; }`, `void SeedUnresolved(IEnumerable<string> paths)`, `void SaveNow()`, `IDisposable`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogTail.UI.Tests/ViewModels/SessionTrackerTests.cs`:

```csharp
using System.Collections.ObjectModel;
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~SessionTrackerTests"`
Expected: FAIL — `SessionTracker` does not exist (compile error).

- [ ] **Step 3: Implement `SessionTracker`**

Create `src/LogTail.UI/ViewModels/SessionTracker.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;

namespace LogTail.UI.ViewModels;

public sealed class SessionTracker : IDisposable
{
    private readonly SessionStore _store;
    private readonly ObservableCollection<TabViewModel> _tabs;
    private readonly List<string> _unresolved = new();
    private readonly CompositeDisposable _subscriptions = new();
    private TabViewModel? _currentSelected;

    public SessionTracker(
        SessionStore store,
        ObservableCollection<TabViewModel> tabs,
        IObservable<TabViewModel?> selectedTab,
        ILogTailLogger? logger = null)
    {
        _store = store;
        _tabs = tabs;

        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _tabs.CollectionChanged += handler,
                handler => _tabs.CollectionChanged -= handler)
            .Subscribe(_ => OnChanged())
            .DisposeWith(_subscriptions);

        selectedTab
            .Subscribe(tab =>
            {
                _currentSelected = tab;
                OnChanged();
            })
            .DisposeWith(_subscriptions);
    }

    public bool IsEnabled { get; set; }

    public void SeedUnresolved(IEnumerable<string> paths)
    {
        _unresolved.Clear();
        _unresolved.AddRange(paths);
    }

    public void SaveNow()
    {
        if (!IsEnabled)
        {
            return;
        }

        var files = new List<string>();
        foreach (var tab in _tabs)
        {
            AddIfMissing(files, tab.FilePath);
        }

        foreach (var path in _unresolved)
        {
            AddIfMissing(files, path);
        }

        _store.Save(new SessionState(files, _currentSelected?.FilePath));
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
    }

    private void OnChanged()
    {
        SaveNow();
    }

    private static void AddIfMissing(List<string> paths, string candidate)
    {
        if (!paths.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            paths.Add(candidate);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~SessionTrackerTests"`
Expected: PASS (4 tests green).

---

### Task 5: UI — `MainWindowViewModel` (RestoreTabs, Settings, remove Theme)

**Files:**
- Modify: `src/LogTail.UI/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `SettingsViewModel` (Task 3).
- Produces: `MainWindowViewModel(SettingsStore settings, ILogSourceFactory sourceFactory, ILogTailLogger? logger = null, SettingsViewModel? settingsViewModel = null)`; `public SettingsViewModel Settings { get; }`; `public Interaction<Unit, Unit> ShowSettingsDialog { get; }`; `public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }`; `public void RestoreTabs(IReadOnlyList<string> paths, string? activePath)`. Removes `CurrentTheme` and `SetThemeCommand`.

- [ ] **Step 1: Write the failing test and remove the obsolete ones**

In `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`:

1. Delete the entire `SetThemeCommand_WhenExecuted_UpdatesCurrentTheme` fact.
2. In `Constructor_WhenInitialized_HasDefaultProperties`, delete the line `sut.CurrentTheme.Should().Be(ThemeMode.System);`.
3. Add this fact:

```csharp
    [Fact]
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: FAIL — `RestoreTabs` does not exist and `CurrentTheme`/`SetThemeCommand` still referenced/needed.

- [ ] **Step 3: Modify `MainWindowViewModel`**

In `src/LogTail.UI/ViewModels/MainWindowViewModel.cs`:

1. Add `using System.Collections.Generic;` at the top (alongside the existing `System.Collections.ObjectModel` import).

2. Remove the `_currentTheme` field and the whole `CurrentTheme` property block (lines declaring `private ThemeMode _currentTheme;` and `public ThemeMode CurrentTheme ...`).

3. Remove `public ReactiveCommand<ThemeMode, Unit> SetThemeCommand { get; }` and the `SetThemeCommand = ReactiveCommand.Create<ThemeMode>(SetTheme);` line in the constructor.

4. Remove the private method:
```csharp
    private void SetTheme(ThemeMode mode)
    {
        CurrentTheme = mode;
        _settings.Update(s => s with { Theme = mode });
    }
```

5. Add these members next to the existing `ShowOpenFileDialog` interaction (near the commands):
```csharp
    public SettingsViewModel Settings { get; }

    public Interaction<Unit, Unit> ShowSettingsDialog { get; } = new();

    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
```

6. Change the constructor signature and wire the new members. Replace:
```csharp
    public MainWindowViewModel(SettingsStore settings, ILogSourceFactory sourceFactory, ILogTailLogger? logger = null)
    {
        _settings = settings;
        _sourceFactory = sourceFactory;
        _logger = logger;

        // Restore settings.
        var loaded = _settings.Load();
        CurrentTheme = loaded.Theme;
        var initial = loaded.BufferCapacity > 0 ? loaded.BufferCapacity : 50_000;
```
with:
```csharp
    public MainWindowViewModel(
        SettingsStore settings,
        ILogSourceFactory sourceFactory,
        ILogTailLogger? logger = null,
        SettingsViewModel? settingsViewModel = null)
    {
        _settings = settings;
        _sourceFactory = sourceFactory;
        _logger = logger;
        Settings = settingsViewModel ?? new SettingsViewModel(settings, logger);

        // Restore settings.
        var loaded = _settings.Load();
        var initial = loaded.BufferCapacity > 0 ? loaded.BufferCapacity : 50_000;
```

7. In the constructor command wiring, replace:
```csharp
        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        ClearCommand = ReactiveCommand.Create(Clear);
        SetThemeCommand = ReactiveCommand.Create<ThemeMode>(SetTheme);
```
with:
```csharp
        OpenFileCommand = ReactiveCommand.CreateFromTask(OpenFileAsync);
        ClearCommand = ReactiveCommand.Create(Clear);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(
            async () => await ShowSettingsDialog.Handle(Unit.Default));
```

8. Add the batch restore method (place it right after `AddTab`):
```csharp
    /// <summary>
    /// Restore a saved session in one batch: add every tab first, then select
    /// the active one once, so the SelectedTab observer starts a single tail
    /// instead of restarting per tab.
    /// </summary>
    public void RestoreTabs(IReadOnlyList<string> paths, string? activePath)
    {
        if (paths.Count == 0)
        {
            return;
        }

        TabViewModel? active = null;
        foreach (var path in paths)
        {
            if (FindTabByPath(path) is not null)
            {
                continue;
            }

            var tab = new TabViewModel(path);
            AttachLinesPerSecondCounter(tab);
            Tabs.Add(tab);

            if (activePath is not null &&
                string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase))
            {
                active = tab;
            }
        }

        SelectedTab = active ?? Tabs.LastOrDefault();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests"`
Expected: PASS — `RestoreTabs_WhenCalled_AddsTabsInOrderAndSelectsActive` green and no compile errors from removed members.

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build LogTail.slnx`
Expected: Build succeeded. (`App.axaml.cs` still subscribes to `viewModel.CurrentTheme` at this point — if it fails to compile, proceed to Task 6 immediately; that subscription is removed in Task 7. To keep this task compiling, temporarily replace that subscription block now: delete the `viewModel.WhenAnyValue(x => x.CurrentTheme).ObserveOn(...).Subscribe(ApplyTheme);` lines and instead call `ApplyTheme(settings.Load().Theme);` using the existing local `settings` variable. Task 7 replaces this with the `SettingsViewModel` wiring.)

---

### Task 6: UI — `SettingsWindow` + gear icon + remove View menu

**Files:**
- Create: `src/LogTail.UI/Views/SettingsWindow.axaml`
- Create: `src/LogTail.UI/Views/SettingsWindow.axaml.cs`
- Modify: `src/LogTail.UI/Views/MainWindow.axaml`
- Modify: `src/LogTail.UI/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel` (Task 3), `MainWindowViewModel.ShowSettingsDialog` and `OpenSettingsCommand` (Task 5).
- Produces: `SettingsWindow` modal dialog.

- [ ] **Step 1: Create `SettingsWindow.axaml`**

Create `src/LogTail.UI/Views/SettingsWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:LogTail.UI.ViewModels"
        x:Class="LogTail.UI.Views.SettingsWindow"
        x:DataType="vm:SettingsViewModel"
        Title="Settings"
        Width="420"
        Height="240"
        CanResize="False"
        WindowStartupLocation="CenterOwner">
  <StackPanel Margin="16" Spacing="14">
    <StackPanel Spacing="6">
      <TextBlock Text="Theme" FontWeight="SemiBold" />
      <ComboBox ItemsSource="{Binding ThemeOptions}"
                SelectedItem="{Binding Theme, Mode=TwoWay}"
                Width="200"
                HorizontalAlignment="Left" />
    </StackPanel>
    <CheckBox Content="Reopen last session on startup"
              IsChecked="{Binding RestoreLastSession, Mode=TwoWay}" />
    <Button Content="Close"
            HorizontalAlignment="Right"
            Click="OnCloseClick" />
  </StackPanel>
</Window>
```

- [ ] **Step 2: Create `SettingsWindow.axaml.cs`**

Create `src/LogTail.UI/Views/SettingsWindow.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LogTail.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
```

- [ ] **Step 3: Update `MainWindow.axaml`**

Replace the existing top `Menu` block:

```xml
      <Menu DockPanel.Dock="Top">
        <MenuItem Header="_File">
          <MenuItem Header="_Open..." Command="{Binding OpenFileCommand}" />
          <Separator />
          <MenuItem Header="E_xit" />
        </MenuItem>
        <MenuItem Header="_View">
          <MenuItem Header="_Theme">
            <MenuItem Header="System" Command="{Binding SetThemeCommand}" CommandParameter="{x:Static models:ThemeMode.System}" />
            <MenuItem Header="Light" Command="{Binding SetThemeCommand}" CommandParameter="{x:Static models:ThemeMode.Light}" />
            <MenuItem Header="Dark" Command="{Binding SetThemeCommand}" CommandParameter="{x:Static models:ThemeMode.Dark}" />
          </MenuItem>
        </MenuItem>
      </Menu>
```

with a top row Grid holding the File menu and a gear button on the right:

```xml
      <Grid DockPanel.Dock="Top" ColumnDefinitions="*,Auto">
        <Menu Grid.Column="0">
          <MenuItem Header="_File">
            <MenuItem Header="_Open..." Command="{Binding OpenFileCommand}" />
            <Separator />
            <MenuItem Header="E_xit" />
          </MenuItem>
        </Menu>
        <Button Grid.Column="1"
                Command="{Binding OpenSettingsCommand}"
                Background="Transparent"
                BorderThickness="0"
                Padding="8,4"
                ToolTip.Tip="Settings"
                VerticalAlignment="Center">
          <Path Width="16"
                Height="16"
                Stretch="Uniform"
                Fill="{DynamicResource SystemControlForegroundBaseHighBrush}"
                Data="M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z" />
        </Button>
      </Grid>
```

If `xmlns:models="using:LogTail.Core.Models"` becomes unused after removing the menu, leave the namespace declaration in place (harmless) or remove it.

- [ ] **Step 4: Register the dialog handler in `MainWindow.axaml.cs`**

Inside `this.WhenActivated(disposables => { ... })`, right after the `ShowOpenFileDialog.RegisterHandler(...)` line, add:

```csharp
            ViewModel?.ShowSettingsDialog.RegisterHandler(DoShowSettingsAsync)
                .DisposeWith(disposables);
```

Then add this method (next to `DoShowOpenFileDialogAsync`):

```csharp
    private async Task DoShowSettingsAsync(IInteractionContext<Unit, Unit> interaction)
    {
        if (ViewModel is null)
        {
            interaction.SetOutput(Unit.Default);
            return;
        }

        var window = new SettingsWindow
        {
            DataContext = ViewModel.Settings
        };

        await window.ShowDialog(this);
        interaction.SetOutput(Unit.Default);
    }
```

- [ ] **Step 5: Build and smoke-check**

Run: `dotnet build LogTail.slnx`
Expected: Build succeeded.

Run: `dotnet run --project src/LogTail.UI/LogTail.UI.csproj`
Expected: window shows `File` menu and a gear button at top-right; clicking gear opens the Settings dialog with a Theme dropdown and a "Reopen last session on startup" checkbox; changing Theme updates the UI immediately; Close dismisses it.

---

### Task 7: UI — `App` wiring (theme + session restore)

**Files:**
- Modify: `src/LogTail.UI/App.axaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel` (Task 3), `SessionStore` (Task 2), `SessionTracker` (Task 4), `MainWindowViewModel.RestoreTabs`/`Settings` (Task 5), `LogFileValidator`, `LogTail.Core.Models.SessionState`.
- Produces: runtime startup behavior (restore / explicit-file precedence / tracker lifecycle).

- [ ] **Step 1: Rewrite `OnFrameworkInitializationCompleted`**

Replace the body of `OnFrameworkInitializationCompleted` in `src/LogTail.UI/App.axaml.cs` with:

```csharp
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Build dependencies.
            var logger = new ConsoleLogger();

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "log-tail");
            Directory.CreateDirectory(appDataDir);

            var settings = new SettingsStore(appDataDir, logger);
            var sessionStore = new SessionStore(appDataDir, logger);
            var factory = new LogSourceFactory(logger);

            var settingsViewModel = new SettingsViewModel(settings, logger);

            // Apply saved theme and keep it in sync with the Settings dialog.
            ApplyTheme(settingsViewModel.Theme);
            settingsViewModel.ThemeChanged += ApplyTheme;

            var viewModel = new MainWindowViewModel(settings, factory, logger, settingsViewModel);

            // Session tracking. Created before restore so it observes the tabs
            // that restore adds, but disabled until restore finishes to avoid
            // writing mid-restore.
            var tracker = new SessionTracker(
                sessionStore,
                viewModel.Tabs,
                viewModel.WhenAnyValue(x => x.SelectedTab),
                logger);

            // Explicit startup file (headless demo env var; CLI args later) wins
            // over session restore.
            var autoOpen = Environment.GetEnvironmentVariable("LOGTAIL_AUTO_OPEN_FILE");
            var hasExplicitFile = !string.IsNullOrEmpty(autoOpen) && File.Exists(autoOpen);

            if (hasExplicitFile)
            {
                viewModel.CurrentFilePath = autoOpen;
                _ = viewModel.OpenFileAndAddTabAsync(autoOpen!);
            }
            else if (settingsViewModel.RestoreLastSession)
            {
                RestoreSession(viewModel, sessionStore, tracker);
            }

            tracker.IsEnabled = settingsViewModel.RestoreLastSession;
            settingsViewModel.RestoreLastSessionChanged += enabled => tracker.IsEnabled = enabled;
            desktop.Exit += (_, _) => tracker.Dispose();

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RestoreSession(
        MainWindowViewModel viewModel,
        SessionStore sessionStore,
        SessionTracker tracker)
    {
        var session = sessionStore.Load();
        if (session.Files.Count == 0)
        {
            return;
        }

        var valid = new List<string>();
        var missing = new List<string>();
        foreach (var path in session.Files)
        {
            if (LogFileValidator.TryValidateFile(path, out _))
            {
                valid.Add(path);
            }
            else
            {
                missing.Add(path);
            }
        }

        tracker.SeedUnresolved(missing);
        viewModel.RestoreTabs(valid, session.ActiveFile);

        if (missing.Count > 0)
        {
            viewModel.StatusMessage = $"{missing.Count} files from the last session were not found";
        }
    }
```

- [ ] **Step 2: Add the missing using**

Add `using System.Collections.Generic;` at the top of `src/LogTail.UI/App.axaml.cs`.

Remove the now-obsolete `viewModel.WhenAnyValue(x => x.CurrentTheme)...` subscription block if it is still present (Task 5 Step 5 may have temporarily replaced it). Removing `CurrentTheme` from the view model means this block must not remain.

- [ ] **Step 3: Build**

Run: `dotnet build LogTail.slnx`
Expected: Build succeeded, no warnings about `CurrentTheme`.

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project src/LogTail.UI/LogTail.UI.csproj`

Then verify:
1. Open two log files (drag-drop or File > Open), switch to the second tab, close the app.
2. Reopen: both tabs return, the second is selected/active.
3. Gear → uncheck "Reopen last session on startup" → reopen app: no tabs restored (empty state).
4. Gear → re-check it → open a file → close → reopen: the newer session restores.
5. Delete one file from a saved session, reopen: that tab is absent, the other is restored, and `%LocalAppData%/log-tail/session.json` still contains the deleted path.
6. Run with `LOGTAIL_AUTO_OPEN_FILE` set to an existing file: only that file opens, session is not restored.

---

### Task 8: Final verification, review, and single commit

**Files:**
- No source changes expected unless a defect is found.

**Interfaces:**
- Consumes: everything above.
- Produces: a green build/test run and one commit.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test LogTail.slnx`
Expected: all tests pass (existing + new). If any test fails, STOP and report — do not auto-fix without approval.

- [ ] **Step 2: Run the project hygiene scan**

Run the project hygiene check defined in the repo's `AGENTS.md`: scan tracked files for absolute user-home path patterns (Windows drive-letter user directories and the Unix home-directory prefixes) plus the user's real name. Do not paste the literal patterns or name into any tracked file — run the scan in the terminal only.

Expected: no matches in tracked files. If there is a match, remove the offending machine-specific string before committing.

- [ ] **Step 3: Present the code review**

Review the diff (`git status`, `git diff`) and present a verdict covering correctness, the removed `CurrentTheme`/`SetThemeCommand` surface, session/`settings.json` write separation, and test coverage. Do not commit until the review is presented.

- [ ] **Step 4: Stage and commit (single commit at the end)**

Run: `git status`
Run: `git add src/LogTail.Core/Models/AppSettings.cs src/LogTail.Core/Models/SessionState.cs src/LogTail.Core/Persistence/SessionStore.cs src/LogTail.UI/ViewModels/SettingsViewModel.cs src/LogTail.UI/ViewModels/SessionTracker.cs src/LogTail.UI/ViewModels/MainWindowViewModel.cs src/LogTail.UI/App.axaml.cs src/LogTail.UI/Views/MainWindow.axaml src/LogTail.UI/Views/MainWindow.axaml.cs src/LogTail.UI/Views/SettingsWindow.axaml src/LogTail.UI/Views/SettingsWindow.axaml.cs tests/LogTail.Core.Tests/Persistence/SettingsStoreTests.cs tests/LogTail.Core.Tests/Persistence/SessionStoreTests.cs tests/LogTail.UI.Tests/MainWindowViewModelTests.cs tests/LogTail.UI.Tests/ViewModels/SettingsViewModelTests.cs tests/LogTail.UI.Tests/ViewModels/SessionTrackerTests.cs docs/superpowers/specs/2026-09-11-session-restore-settings-design.md docs/superpowers/plans/2026-09-11-session-restore-settings.md`
Run: `git commit -m "feat(ui,core): restore last session and add settings dialog"`

Expected: one commit containing the spec, plan, implementation, and tests.

---

## Self-Review

**Spec coverage:**
- `RestoreLastSession` preference (default true) → Task 1.
- Separate `session.json` + `SessionStore` → Task 2.
- `SettingsViewModel` owning Theme + restore flag → Task 3.
- `SessionTracker` saving on change, disabled = no writes, unresolved retention → Task 4.
- `RestoreTabs` batch restore, `Settings`, `OpenSettingsCommand`, theme removal → Task 5.
- Settings window + gear + View menu removal → Task 6.
- Startup precedence (explicit file wins), missing-file retention, tracker lifecycle → Task 7.
- Error handling for corrupt/missing session → Task 2 (`Load` returns empty); all-invalid restore → Task 7 (`RestoreTabs` with empty `valid`, status message).
- Tests listed in the spec → Tasks 1–5, plus Tasks 6–8 verification.

**Placeholder scan:** no `TBD`/`TODO`/"add error handling"/"similar to Task N"; every code step shows full code.

**Type consistency:** `SessionState.Files`/`ActiveFile`, `SessionStore.Load/Save/Update`, `SettingsViewModel.Theme/RestoreLastSession/ThemeChanged/RestoreLastSessionChanged/ThemeOptions`, `SessionTracker.IsEnabled/SeedUnresolved/SaveNow`, and `MainWindowViewModel.RestoreTabs(IReadOnlyList<string>, string?)` use the same names and signatures across all tasks.

**Deviation from spec:** `MainWindowViewModel.RestoreTabs` is `public` (the spec said `internal`) so the UI test project can call it without an `InternalsVisibleTo` change.
