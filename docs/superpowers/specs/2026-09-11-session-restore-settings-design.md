# Session Restore + Settings Window Design

**Date:** 2026-09-11
**Status:** Implemented
**Scope:** Core (`LogTail.Core`), UI (`LogTail.UI`), Core Tests (`LogTail.Core.Tests`), UI Tests (`LogTail.UI.Tests`)

## Problem

Every time LogTail is run, the window opens empty and the user has to reopen the same log file manually. Tabs being worked on in the previous session disappear as soon as the app is closed. In addition, app preferences have no dedicated place — Theme can only be changed through the `View > Theme` menu, and there is no container for other preferences such as startup behavior.

## Goal

1. On startup, restore all tabs from the last session along with the most recently active tab.
2. Do not rely solely on the last file: if a file from the session is missing (moved/deleted/drive not yet mounted), that tab is skipped but its path is preserved so it can be restored in a later session.
3. Provide an on/off option for restore behavior, **enabled by default**.
4. The OFF option means *pause*: stop restoring and stop saving, but old session data is not deleted.
5. Provide a Settings window that can hold preferences (starting with Theme, moved out of the menu `View`, plus the restore option), opened via the gear icon in the top right corner.
6. If LogTail is run with an explicit file, the explicit file wins and session restore is skipped.

## Design

### 1. Data Model & Storage

`AppSettings` (`src/LogTail.Core/Models/AppSettings.cs`) gains a single preference:

```csharp
public sealed record AppSettings(
    ThemeMode Theme = ThemeMode.System,
    int BufferCapacity = 50_000,
    int MaxBufferCapacity = 2_000_000,
    TimeSpan PollInterval = default,
    string DefaultEncoding = "utf-8",
    bool RestoreLastSession = true);
```

`RestoreLastSession` defaults to `true`. Deserializing an old `settings.json` that does not yet have this field still yields the default value (System.Text.Json uses the default parameter constructor).

Volatile session state is stored separately in `session.json`, in the same folder as `settings.json` (namely `%LocalAppData%/log-tail/`):

```csharp
// src/LogTail.Core/Models/SessionState.cs
public sealed record SessionState(
    IReadOnlyList<string> Files,
    string? ActiveFile);
```

The new `SessionStore` class (`src/LogTail.Core/Persistence/SessionStore.cs`) mirrors `SettingsStore` (`Load`/`Save`/`Update`), returns an empty `SessionState` (`Files` empty, `ActiveFile` null) if the file does not exist or is corrupt, and logs a warning via `ILogTailLogger` without crashing.

WHY it is separate from `settings.json`: the session is written on every tab change. If merged, every tab switch would rewrite Theme/Buffer and could race with Theme changes from the Settings window. Two separate files mean two independent write domains that do not overwrite each other.

### 2. `SessionTracker` (Save Orchestration)

The new `SessionTracker` class (`src/LogTail.UI/ViewModels/SessionTracker.cs`, one type per file) is created so that `MainWindowViewModel` does not grow bloated:

```csharp
public sealed class SessionTracker : IDisposable
{
    public SessionTracker(
        SessionStore store,
        ObservableCollection<TabViewModel> tabs,
        IObservable<TabViewModel?> selectedTab,
        ILogTailLogger? logger = null);

    public bool IsEnabled { get; set; }
    public void SeedUnresolved(IEnumerable<string> paths);
    public void SaveNow();
    public void Dispose();
}
```

Behavior:

- Subscribe to `tabs.CollectionChanged` and `selectedTab` changes. On every event, if `IsEnabled`, write the session.
- `SaveNow()` builds `Files` = tab paths in order **merged** with the seeded *unresolved* paths (case-insensitive dedup), and `ActiveFile` = `SelectedTab?.FilePath`.
- `IsEnabled = false` (OFF option) → no reads, no writes. Old data on disk is left intact.
- Toggling OFF → ON only resumes; it does not snapshot immediately. A new write happens on the next tab change. This satisfies the "OFF = pause" choice: if it is turned ON and then restarted with no tab change, the old session is still restored.
- Unresolved paths never become tabs, so they are always included in the next `SaveNow()` (never removed) — this is what keeps a missing file restorable.

### 3. Startup Restore & Missing-File Retention

In `src/LogTail.UI/App.axaml.cs` (`OnFrameworkInitializationCompleted`), the order is:

1. Detect `explicitFile` (currently from the env `LOGTAIL_AUTO_OPEN_FILE`; in the future from a CLI arg). If present and the file is valid → open only that file, **skip** session restore (satisfies "explicit file wins"). Opening the explicit file triggers a normal save, so the saved session switches to that file — reasonable, because the session reflects what is currently in use.
2. If there is no `explicitFile` **and** `RestoreLastSession == true`:
   - `sessionStore.Load()`.
   - Split `Files` into valid / missing using `LogFileValidator.TryValidateFile`.
   - Call `MainWindowViewModel.RestoreTabs(validPaths, activePath)` (see section 4) then `tracker.SeedUnresolved(missingPaths)`.
   - If any are missing, set `StatusMessage`, e.g. `"2 files from the last session were not found"`.
3. If `RestoreLastSession == false` → do not read the session at all. `SessionTracker` is still created but with `IsEnabled = false`, so there is no restore or save until the option is turned on.

### 4. Changes to `MainWindowViewModel`

`src/LogTail.UI/ViewModels/MainWindowViewModel.cs`:

- Add the batch method `internal void RestoreTabs(IReadOnlyList<string> paths, string? activePath)`:
  - Add all `TabViewModel` (each via `AttachLinesPerSecondCounter`) **without** setting `SelectedTab` per item.
  - Set `SelectedTab` **once** at the end to the tab matching `activePath`, or the last valid tab if `activePath` is not in the list. This avoids N start/stop tail cycles because the `SelectedTab` observer already exists.
- Add `Interaction<Unit, Unit> ShowSettingsDialog` + `OpenSettingsCommand` (same pattern as `ShowOpenFileDialog`).
- Add the property `SettingsViewModel Settings { get; }` (injected via the constructor).
- **Delete** `CurrentTheme` and `SetThemeCommand` — after the `View > Theme` menu is deleted, they are no longer used by the view; Theme ownership moves to `SettingsViewModel`.

### 5. `SettingsViewModel` + Settings Window

New `SettingsViewModel` class (`src/LogTail.UI/ViewModels/SettingsViewModel.cs`):

```csharp
public sealed class SettingsViewModel : ReactiveObject
{
    public SettingsViewModel(SettingsStore settings, ILogTailLogger? logger = null);

    public ThemeMode Theme { get; set; }              // set → persist + raise + event
    public bool RestoreLastSession { get; set; }      // set → persist + raise + event
    public IReadOnlyList<ThemeMode> ThemeOptions { get; }  // [System, Light, Dark]

    public event Action<ThemeMode>? ThemeChanged;
    public event Action<bool>? RestoreLastSessionChanged;
}
```

Every setter calls `SettingsStore.Update` and raises the related event. Initial values are read from the stored `AppSettings`.

New window `SettingsWindow.axaml` + `SettingsWindow.axaml.cs` (`src/LogTail.UI/Views/`): a modal dialog without resizing, containing:
- ComboBox Theme (`ItemsSource = ThemeOptions`, `SelectedItem` two-way bound to `Theme`).
- CheckBox "Reopen last session on startup" two-way bound to `RestoreLastSession`.
- Close button.

Theme changes are applied directly (live), matching the previous menu behavior.

### 6. Layout Gear + Wiring `App`

`src/LogTail.UI/Views/MainWindow.axaml`:
- The top row becomes `Grid ColumnDefinitions="*,Auto"` — `Menu` in column 0, the gear button (`PathIcon` with gear geometry) in column 1 (top right corner).
- Delete the `View` submenu entirely (Theme moves to Settings). The only remaining menu is `File` (Open…, Separator, Exit).

`src/LogTail.UI/Views/MainWindow.axaml.cs`:
- Register the `ShowSettingsDialog` handler in `WhenActivated`, open `SettingsWindow` with `ViewModel.Settings` as the DataContext via `ShowDialog(this)`.

`src/LogTail.UI/App.axaml.cs`:
- Create one shared `SettingsViewModel`, `ApplyTheme(settingsVm.Theme)` at start, subscribe `ThemeChanged → ApplyTheme`.
- Create `SessionStore` + `SessionTracker` (`IsEnabled = settingsVm.RestoreLastSession`, observing `viewModel.Tabs` and `viewModel.WhenAnyValue(x => x.SelectedTab)`).
- Subscribe `RestoreLastSessionChanged → tracker.IsEnabled`.
- Dispose the tracker on `desktop.Exit`.

WHY Theme is centered in `SettingsViewModel`: there is a single owner of Theme state, so `App` applies the theme from one source and no two paths can fall out of sync.

## Error Handling

- `session.json` corrupt / unreadable → empty `SessionState` + warning log, the app keeps running.
- Failed to write `session.json` → warning log, ignored (does not disturb tailing).
- All restore paths invalid → empty window + `StatusMessage` informs the user.
- Option OFF → no restore, no save, old data intact.
- `SettingsStore`/`SessionStore` each write only their own file; `SessionTracker` is invoked from the UI thread, so there is no write race.

## Testing

Automated:

Core (`LogTail.Core.Tests`):
1. `SessionStore`: `Load` when the file does not exist yet → empty `SessionState`.
2. `SessionStore`: `Save` then `Load` → roundtrip of `Files` and `ActiveFile`.
3. `SessionStore`: corrupt file → empty `SessionState`.
4. `AppSettings`: `RestoreLastSession` default `true`.
5. `AppSettings`: roundtrip of `RestoreLastSession = false`.

UI (`LogTail.UI.Tests`):
6. `SettingsViewModel`: initial Theme is read from the default store (`System`).
7. `SettingsViewModel`: set `Theme` → saved in `settings.json` + `ThemeChanged` raised.
8. `SettingsViewModel`: set `RestoreLastSession` → saved + `RestoreLastSessionChanged` raised.
9. `SessionTracker`: `IsEnabled = true` + tab collection change → `session.json` contains the tab paths.
10. `SessionTracker`: `SelectedTab` change → `ActiveFile` is updated too.
11. `SessionTracker`: `IsEnabled = false` → no writes at all.
12. `SessionTracker`: `SeedUnresolved` → missing paths remain in the session alongside valid tabs.
13. `MainWindowViewModel.RestoreTabs`: adds tabs in the correct order, selects the correct active tab.
14. Update old tests that touch `CurrentTheme`/`SetThemeCommand` so they target `SettingsViewModel`.

Manual smoke:
- Click the gear → the Settings window opens; change the Theme → the view changes immediately.
- Toggle restore OFF → restart → empty window; ON → restart → tabs return.
- Delete one of the session files then restart → that tab is gone, the other tabs return, `session.json` still stores the missing path.
- Run with an explicit file (env `LOGTAIL_AUTO_OPEN_FILE`) → only that file opens, the session is not restored.

## Out of Scope

- Restoring window size/position, scroll viewer position, and per-tab AutoScroll state.
- Recent files history menu.
- "Clear session" button (may follow later).
- CLI args implementation (`--new-window`, wildcard, folder) — handled by a separate spec.
- Non-modal Settings, or more than one Settings window.
- Automatic pruning of permanently missing paths.

## Risks

1. **Missing paths accumulate permanently:** because they are deliberately retained, `session.json` can store dead paths forever. Accepted for this iteration; manual pruning can be added later.
2. **Order changes:** on save, unresolved paths are placed after the tab paths, so the restored tab order can differ from the original session if a file is missing in the middle. Cosmetic impact.
3. **VM contract change:** `CurrentTheme`/`SetThemeCommand` are removed from `MainWindowViewModel`, so old tests must be adjusted. This is a direct consequence of the requested Theme move.

## Implementation Order

1. Core: `SessionState` record + field `RestoreLastSession` in `AppSettings`.
2. Core: `SessionStore` (Load/Save/Update) + Core tests.
3. UI: `SettingsViewModel` + tests.
4. UI: `SessionTracker` + tests.
5. UI: `MainWindowViewModel.RestoreTabs`, `Settings`, `OpenSettingsCommand`, delete `CurrentTheme`/`SetThemeCommand`; adjust old tests.
6. UI: `SettingsWindow` + gear in `MainWindow`, delete the `View` menu.
7. UI: wiring `App` (theme + session restore + tracker + event).
8. Manual smoke test per the list above.
