# Workspace Folder Tabs — Drag Folder as a Folder Tab

**Date:** 2026-09-26 (revised; supersedes the 2026-09-24 flat-list sidebar design)
**Status:** Approved by user
**Scope:** Core (`SessionState`) + UI (`ViewModels`, `MainWindow.axaml`, `MainWindow.axaml.cs`, `App.axaml.cs`)

## 1. Goal

Drag a **folder** → it opens as its own **folder tab**. The left sidebar lists the open workspace folders; the folder tab's content is a **tree** of the log files inside it (folder structure preserved). Drag a **file** → unchanged: it opens as a log tab.

Each folder tab owns its workspace. Closing a folder tab drops it from the sidebar and from the saved session. Workspace roots survive restart, like `SessionState.Files` does today.

> ponytail: one folder tab per dropped folder, no nesting folders inside folders. `TabViewModel.IsFolder` flag instead of a parallel tab type — same `TabControl`, same tab strip.

## 2. UX

### 2.1 Layout

```
┌────────────────────────────────────────────────────────────┐
│ [app (logs)] [web (logs)] [app.log] [error.log]  ← tabs     │
├──────────────┬─────────────────────────────────────────────┤
│ app (logs)   │  folder tab active:                         │
│ Log files    │    app (logs)                               │
│  app.log     4.2 MB   09-28 20:03                        │
│  error.log   919 KB   09-24 00:00                          │
│  access.jsonl 12.4 MB 09-24 08:10                         │
│ +12 file lain│  log tab active: log lines (unchanged)      │
└──────────────┴─────────────────────────────────────────────┘
```

- **One shared tab strip.** Folder tabs and log tabs live in the same `TabControl`, in **drop order** — sorting or grouping tabs by kind is not part of this design.
- **Tabs switch folders.** Each folder tab is a workspace; selecting a tab switches the sidebar to that folder. Folder tab header shows `name (parent)` so two folders with the same name stay distinguishable.
- **Sidebar (fixed 300px) = the log files inside the current folder**, flat, sorted by name. Each row is `name | size | modified`; the name is ellipsized, the full path and timestamp are the tooltip. Double-click a file → opens a log tab. Header shows the folder label plus a `Log files` caption. Hidden when no folder tab is open (`IsVisible={Binding HasWorkspaces}`).
- **Row layout is hard-coded, not content-driven**: root grid `ColumnDefinitions="300,*"` and row columns `160,56,62` with 5px spacing (160+56+62+2×5 = 288 = 300 minus the item's 6px padding per side). A star column resolves against its content and a `Width` binding to the scroll viewport is not guaranteed to resolve, so both let the row follow the longest file name; hard widths cannot.
- **Rows paint no background at rest.** The Fluent theme fills every `ListBoxItem` with `#363636` across the full row, which reads as a grey slab beside the `#0F0E0E` sidebar whenever a name is short. Overriding the item's `Background` does nothing because the theme paints the template `Border`, so the override targets `ListBoxItem /template/ Border`. A plain selector still loses to the theme's `:selected` / `:pointerover` variants, so selection and hover survive. A 1px `#20808080` bottom border keeps rows legible.
- **The sidebar keeps listing while a log tab is selected** — the navigator stays available when reading a log, so a log tab does not empty it. It is empty only when every folder tab is closed.
- **Folder tab content**: a workspace summary (folder label, full path, a pointer to the sidebar) until a file is picked; the log lines of the picked file after that. The tab is a workspace switcher, not a file browser.
- **Log tab content = existing log view**, untouched. A log tab is a viewer slot rather than a pinned file: a single click in the sidebar retargets the tab on screen, clearing the previous file's lines and tailing the new one from the top.

### 2.2 Interactions

| Action | Result |
|---|---|
| Drop folder | `OpenFolderTab(path)`; same folder already open → reuse that tab, no duplicate |
| Drop file(s) | Log tabs (unchanged) |
| Click folder row in sidebar | n/a — the sidebar lists files, not folders; switching folder is a tab click |
| Double-click file in sidebar | `OpenFileAndAddTabAsync(path)`; already open → select existing tab |
| **Single click** file in sidebar | Show it in the tab that is on screen: preview it inside a folder tab, or retarget a log tab at it. No new tab. |
| Close folder tab | Folder removed from sidebar list, `WorkspaceFolders` on save, watcher disposed |
| Close log tab | Unchanged |

Mixed drop: every folder becomes its own tab, every file becomes its own log tab — in drop order.

> ponytail: the sidebar folder list duplicates the folder tabs on purpose — it is the "which workspace am I in" switch, without scanning the tab strip for folder names. Remove it later if it feels redundant.

### 2.3 File list contents

- Recursive walk from the workspace root, collecting files with extension `.log`, `.txt`, `.jsonl` (case-insensitive). `stdlib` only.
- **Flat**: folder structure is not shown. Switching folders is what tabs are for; a hierarchy would add a navigation level for nothing.
- **Sort**: by file name, `OrdinalIgnoreCase`, with the full path breaking ties so two same-named files in different subfolders land in a stable order.
- **Cap 1000 files per workspace.** Overflow is counted and shown as `+N file lain tidak ditampilkan` at the bottom of the list.
- **Scanning runs off the UI thread.** A directory walk over a large workspace blocks the dispatcher, and a blocked dispatcher means buffered log events never render — the log looks frozen while its source is still tailing. A generation counter drops the result of a scan whose folder the user has already navigated away from.

## 3. State & Persistence

### 3.1 Model

```csharp
// LogTail.Core/Models/SessionState.cs
public sealed record SessionState(
    IReadOnlyList<string> Files,                        // log file tabs only, in tab order
    string? ActiveFile,
    IReadOnlyList<string>? WorkspaceFolders = null,     // folder tabs, in tab order
    string? ActiveFolder = null);                       // selected folder tab path
```

- `Files` never contains folder paths. `LogFileValidator.TryValidateFile` rejects directories, so leaving folder paths in `Files` would resurrect them as "missing files" on restore.
- The earlier single `WorkspaceFolder` field is **dropped** — it was never released, so no migration is needed. `= null` defaults keep any older `session.json` loading without error.

### 3.2 Tracker

- `SessionTracker` gains `WorkspaceFolders` (list, order preserved) + `ActiveFolder`, and `SetWorkspaces(list, active)` → `OnChanged()` → `SaveNow()`.
- `SeedWorkspaces(list, active)` for restore — seeds without saving (same reason `SeedUnresolved` exists).
- `SaveNow()` splits `Tabs` into file paths (`IsFolder == false`) and folder paths (`IsFolder == true`), so the two session fields never mix.
- `ActiveFile` stays the selected **log** tab; `ActiveFolder` is the selected **folder** tab. Closing the active tab of either kind clears that field (existing behavior for `ActiveFile` is reused).

### 3.3 Restore (App.axaml.cs)

```
state = sessionStore.Load()
if (settings.RestoreLastSession) {
  foreach (f in state.WorkspaceFolders) if (Directory.Exists(f)) viewModel.OpenFolderTab(f)  // folders first
  foreach (f in state.Files)          if (LogFileValidator.TryValidateFile(f, out _)) viewModel.AddTab(f)
  viewModel.SelectedTab = state.ActiveFolder is not null ? folder tab : log tab for state.ActiveFile
}
```

- Folder tabs first so the sidebar and trees exist before log tabs are restored.
- Missing folder on disk → folder tab skipped silently; the stale entry disappears on the next save.
- `SeedWorkspaces` before enabling the tracker, so restore does not rewrite the session it just read.

### 3.4 FileWatcher

- **One watcher at a time**, bound to the **selected folder tab**. Selecting another folder tab or closing it disposes and resubscribes.
- `NotifyFilter = FileName | DirectoryName`, `IncludeSubdirectories = true`, `Filter = "*.*"`.
- Debounce 500 ms → re-enumerate the tree for that folder.
- Walk errors (unauthorized, deleted mid-walk) → skip that subtree, set `StatusMessage` once, keep the partial tree.

## 4. Implementation

| File | Change |
|---|---|
| `src/LogTail.Core/Workspace/WorkspaceFileScanner.cs` | **New** — `WorkspaceFile(Name, Path)` plus a manual recursive walk, `MaxFiles` cap with an overflow count, name sort with a path tiebreak, reparse-point and depth guards. Lives in Core, not the UI project: it is plain file-system logic, so its tests need no Avalonia dispatcher. |
| `src/LogTail.Core/Models/SessionState.cs` | `WorkspaceFolders` + `ActiveFolder`; drop `WorkspaceFolder` |
| `src/LogTail.UI/ViewModels/TabViewModel.cs` | `IsFolder` flag, `Preview` + `SetPreview`, `LogSource`/`ShowsLog` for the view, `LoadFile` to retarget a log tab |
| `src/LogTail.UI/ViewModels/MainWindowViewModel.cs` | `OpenFolderTab`, `RestoreWorkspaces`, `WorkspaceFiles` for the listed folder, off-thread scan with a generation guard, single watcher re-aimed on tab switch, `HasWorkspaces`/`WorkspaceTabs`, `ActiveLogTab` (which log is on screen), `ShowWorkspaceFileAsync` (single click), `SelectedWorkspaceFile` (sidebar selection) |
| `src/LogTail.UI/ViewModels/SessionTracker.cs` | workspace list + active folder, file/folder split in `SaveNow` |
| `src/LogTail.UI/Views/MainWindow.axaml` | Sidebar file list with a folder-label header and overflow footer; folder-tab content is a summary panel; tab content switches on `IsFolder` |
| `src/LogTail.UI/Views/MainWindow.axaml.cs` | Drop branch (folder → `OpenFolderTab`, file → `AddTab`); sidebar single click → `ShowWorkspaceFileAsync`, double click → `OpenFileAndAddTabAsync`; the scroll listener tracks `ActiveLogTab` so previewing another file inside the same tab re-attaches |
| `src/LogTail.UI/App.axaml.cs` | Restore folder tabs before log tabs |
| `tests/LogTail.UI.Tests/` | New workspace tests (see §6) |

Reuse unchanged: `LogFileValidator`, `FileTailSource`, `OpenFileAndAddTabAsync`, `FindTabByPath`, `SessionStore`.

## 5. Edge Cases

- Same folder dropped twice → reuse tab (path compare `OrdinalIgnoreCase`).
- Folder deleted while open → tree empties, tab stays, status warns once. Dropping it again reopens cleanly.
- Unauthorized subfolder → skipped, partial tree kept.
- Symlink / junction loop → directory **links are never followed** (each hop through one is a distinct path string, so a visited-path set alone cannot detect the cycle) and a depth cap of 64 is the backstop.
- Empty folder (no matching files) → folder tab opens with an empty tree and no error.
- More than 1000 files → cap + `(+N more)`.
- `RestoreLastSession = false` → no folder tabs, no log tabs (unchanged).
- File dropped onto a folder tab → opens a log tab as usual; the tree is unaffected.

## 6. Tests

File list scanning (pure function, fully testable):
- recursive collection, sorted by name, path breaking ties;
- extension filter is case-insensitive and rejects everything else;
- cap 1000 with the remainder reported as an overflow count;
- directory links are not followed, and a depth limit stops a cycle;
- a missing root reports an error instead of throwing.

Folder tabs and the sidebar:
- `OpenFolderTab` creates one tab with `IsFolder = true` and no tail source;
- `OpenFolderTab` on an already-open folder selects the existing tab (tab count unchanged);
- closing a folder tab leaves log tabs untouched and empties the sidebar when it was the last one;
- closing the listed folder tab falls back to another open folder tab;
- selecting a log tab keeps the sidebar listing the last folder;
- switching folders quickly shows only the last folder's files (stale scan results are dropped).

Folder tab labels:
- a folder tab is labelled `name (parent)`, the parent only at a filesystem root;
- a log tab keeps its bare file name.

Persistence:
- `SaveNow` writes `WorkspaceFolders` + `ActiveFolder` and keeps folder paths out of `Files`;
- closing a folder tab then saving removes it from `WorkspaceFolders`;
- `SessionState` deserializes JSON without the workspace fields (old files) → `null`/empty, no throw;
- restore with a missing folder → folder tab skipped, session still loads.

Not unit tested: watcher debounce timing, drop plumbing (view-level) — manual per §8.

## 7. Non-Goals

Search/filter box; sort by size or mtime; glob patterns; drag a file into the tree to move it; multi-select in the tree; remembering which files were open per workspace; renaming a folder tab; multi-root workspaces (one folder = one tab is enough).

## 8. Verification

1. `dotnet build` and the full test suite pass (run the two test projects separately — the heavy 50k-line test loses its 60s budget when they compete).
2. Drop `C:\logs\app` containing nested subfolders and 3+ log files → a folder tab opens (labelled `app (logs)`), the sidebar lists every matching file recursively, sorted by name.
3. Double-click a file in the sidebar → log tab opens and tails; new lines keep arriving while the folder tab stays open (the scan must not starve the dispatcher).
4. Drop the same folder again → still exactly one folder tab.
5. Drop a second, different folder → two folder tabs; switching tabs switches the sidebar to that folder's files.
6. Close a folder tab → it disappears from the sidebar; restart → still gone.
7. Restart with folders open → folder tabs, trees, and log tabs restore, the active folder tab is selected.
8. Point a workspace at a folder with more than 1000 log files → cap applies and `(+N more)` shows.
9. Drop a single file with no folder open → log tab opens, sidebar stays hidden.
