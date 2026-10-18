# CLI Args + Single-Instance to Tab Design

**Date:** 2026-09-04
**Status:** Abandoned (not implemented)
**Scope:** UI (`LogTail.UI`) + small Core reuse (`LogFileValidator`)

## Problem

`Program.Main(string[] args)` currently forwards `args` to Avalonia via `StartWithClassicDesktopLifetime(args)` but nothing parses them. `App.OnFrameworkInitializationCompleted` never reads startup args, so running `LogTail.exe app.log` opens an empty window and silently drops the file. The only auto-open path is the headless env var `LOGTAIL_AUTO_OPEN_FILE`, which is meant for demo/testing.

User wants: run `LogTail` with file arguments, and if an instance is already running, the new files should appear as tabs in that running process instead of spawning a second window. A hybrid flag should allow forcing a new window.

## Goal

1. Accept one or more file arguments plus `--new-window`, `--help`, `--version`.
2. Support wildcards (`*.log`) and folders (expand to `*.log` + `*.txt`, non-recursive by default).
3. Default to single-instance: second invocation forwards files to the first instance, which opens them as tabs, dedups, selects, and activates its window.
4. Keep current empty-window behaviour when no args are given and never crash on bad args.

## Design

### CLI surface

Syntax:

```
LogTail [options] [paths...]
```

Options:

- `--new-window` — skip forwarding, start an independent window even if another instance runs.
- `--recursive` — when a path is a folder, recurse into subfolders.
- `-h | --help` — print usage and exit 0 without starting UI.
- `--version` — print version and exit 0 without starting UI.

Positional `paths...`:

- Zero paths means old behaviour: open empty, then honour `LOGTAIL_AUTO_OPEN_FILE` if set.
- Each path may be absolute or relative, file, wildcard, or folder.
- Expansion happens in the sender (second process) so the receiver always gets absolute file paths. This keeps validation in one place.
- Folder expansion default is top-level only, matching `*.log` and `*.txt`, sorted alphabetically. With `--recursive`, use `SearchOption.AllDirectories`.
- Wildcard expansion uses `Directory.GetFiles` against the containing directory. On Windows the shell does not expand wildcards, so we must do it manually for cross-platform consistency.
- Missing paths are not fatal. They are forwarded as informational entries so the running instance can show `File not found: <name>` in `StatusMessage`.

WHY sender-side expansion: the receiver already has `AddTab` dedup and `LogFileValidator` checks. Sending absolute files keeps that path unchanged and avoids duplicating validator logic in the sender.

Example:

```
LogTail app.log error.log
LogTail "C:\logs\app 1.log" C:\logs\*.log C:\logs\archived --new-window
LogTail --help
```

Expected `--help` output shape (text may differ slightly):

```
Usage: LogTail [options] [paths...]
  paths         Log files, wildcards (*.log), or folders.
  --new-window  Start a new window instead of reusing the running instance.
  --recursive   Recurse into subfolders when a path is a folder.
  -h, --help    Show this help.
  --version     Show version.
```

### Startup flow in `Program.Main`

New small parser `CliArgsParser` (pure, testable, no IO except expansion) returns a `CliArgs` record:

```csharp
internal sealed record CliArgs(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Missing,
    bool NewWindow,
    bool ShowHelp,
    bool ShowVersion,
    bool Recursive);
```

Flow:

1. Parse args to `CliArgs`. If `ShowHelp` or `ShowVersion`, print and return without building Avalonia.
2. Try to acquire the single-instance mutex `Global\LogTail.SingleInstance` (Windows) or `LogTail.SingleInstance` (Unix). Use a named `Mutex` held for app lifetime.
3. If mutex acquired, we are the owner: continue to `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`, stash `CliArgs.Files` in a static `StartupFiles` holder so `App` can consume after `MainWindow` exists.
4. If mutex NOT acquired and `NewWindow` is false, we are a client: connect to named pipe `logtail-ipc` with 2s timeout, send JSON payload `{ files: [...], missing: [...] }`, exit 0 on success. On timeout or pipe missing (owner died), fall through to become owner (re-acquire) rather than dropping files.
5. If mutex NOT acquired but `NewWindow` is true, skip pipe and start as independent window without holding the global mutex.

WHY mutex + named pipe over OS file association: only an always-on listener can push files into the live `Tabs` collection and call `Activate`. File association alone spawns a new process.

Payload uses JSON over `System.IO.Pipes.NamedPipeClientStream`. Keep it small: string arrays only, no log content. Add a 32-bit length prefix to frame messages for future multi-send.

### Receiver in `App`

`App.OnFrameworkInitializationCompleted` keeps current DI setup, then:

1. After `MainWindow` is assigned to `desktop.MainWindow`, start a background `NamedPipeServerStream` loop (`SingleInstanceServer`) bound to the pipe name above. One thread, `await using` per connection, cancellable on shutdown.
2. On message, `Dispatcher.UIThread.Post` to:
   - For each file in `files`: call `LogFileValidator.TryValidateFile`. If valid, call existing `MainWindowViewModel.AddTab(path)` which already dedups case-insensitively and sets `SelectedTab`. If invalid, collect error.
   - For each entry in `missing`: collect as `File not found` notice.
   - If any errors, set `StatusMessage` to joined first error (avoid flooding).
   - Activate window: if `WindowState == Minimized`, restore to `Normal`, then call `Activate`. Touch `Topmost` only if `Activate` alone does not bring to front on that platform.
3. Drain `StartupFiles` from step above with the same routine, then clear it. CLI files take precedence over `LOGTAIL_AUTO_OPEN_FILE`; if both exist, CLI wins and env var is ignored to avoid double tabs.
4. If `StartupFiles` is empty and env var is set, keep existing auto-open behaviour unchanged.

WHY reuse `AddTab`: it already handles `FindTabByPath`, `AttachLinesPerSecondCounter`, `Tabs.Add`, and tail start via the `SelectedTab` observer. No ViewModel change needed except possibly exposing a thread-safe `OpenFiles(IEnumerable<string>)` helper that loops `AddTab`.

### Activation details

- All UI mutation from pipe thread must go through `Dispatcher.UIThread`.
- `MainWindow.Activate()` is the primary API. Minimized restore is explicit because `Activate` alone does not un-minimize on all platforms.
- No stealing focus loop. One activate per IPC message is enough; repeated messages naturally re-activate.

### Error handling

- Empty args: old path, no IPC, no error.
- Bad flag (e.g. `--foo`): print `Unknown option: --foo`, print usage, exit code 2, do not start UI. Keeps scripts honest.
- File missing at sender: include in `missing`, still forward valid ones. Receiver shows notice but still opens valid tabs.
- Pipe send fails: client becomes owner and opens its own window with its files, so files are never lost when owner crashed but mutex has not released yet.
- Pipe receive malformed JSON: log via `ILogTailLogger`, ignore message, keep server loop alive.
- Second `--new-window` instance never listens on the pipe, so there is exactly one listener. This avoids split-brain where two owners both listen.

### Testing

Core/UI tests, no manual-only paths:

1. `Parser_NoArgs_ReturnsEmpty` — empty files, no flags.
2. `Parser_SingleFile_ResolvesAbsolute` — relative `app.log` becomes absolute under cwd.
3. `Parser_MultipleFiles_PreservesOrder` — order kept for tab order.
4. `Parser_Wildcard_ExpandsSorted` — temp dir with `b.log`, `a.log` yields sorted `a.log`, `b.log`.
5. `Parser_Folder_ExpandsTopLevelOnly` — folder with `x.log`, `y.txt`, `z.bin` yields only `x.log`, `y.txt`; subfolder file excluded without `--recursive`, included with it.
6. `Parser_MissingFile_GoesToMissingList` — nonexistent path lands in `Missing`, not `Files`.
7. `Parser_NewWindowFlag_Parsed` — `--new-window` sets `NewWindow`.
8. `Parser_UnknownOption_ReturnsError` — unknown flag signals usage + exit 2 path.
9. `ViewModel_AddTab_DedupsCaseInsensitive` — existing `AddTab` already covers; add explicit test for IPC-driven double-send of same path selecting existing tab instead of duplicating.
10. `Ipc_Payload_RoundTrips` — serialize then deserialize file + missing lists, verify equality.
11. `Ipc_ClientServer_ForwardsFiles` — in-memory or loopback pipe test with fake handler, verify handler received files.

Manual smoke:

- Start instance A empty, run `LogTail app.log` as B, verify A gains tab, selects it, comes to front, B exits quickly.
- With A running, run `LogTail *.log mydir --recursive`, verify expansion and tab order.
- With A running, run `LogTail big.log --new-window`, verify second window opens independently.
- Kill A abruptly, immediately run `LogTail app.log`, verify new owner starts with tab instead of hanging.

## Out of Scope

- `--recursive` depth limit UI, file watch filters, or exclusion globs.
- Progress UI for bulk open of hundreds of files.
- Single-instance across different user sessions or remote machines.
- Changing `LOGTAIL_AUTO_OPEN_FILE` semantics beyond precedence rule above.
- File association / installer / context-menu entries.

## Risks

1. **Mutex lifetime on crash:** OS releases abandoned mutex, but pipe listener dies with process. Mitigation is client fallback to owner on send timeout, so no silent drop.
2. **Window focus policies:** Linux compositors may ignore `Activate`. Accept degraded mode (tab still opens, user clicks dock icon). No focus-steal loop to fight the compositor.
3. **Bulk open flood:** opening 500 files at once creates 500 tabs and 500 tail subscriptions churn via `SelectedTab` observer. Mitigation is open in arg order but only select the last one at the end, or batch `Tabs.Add` then set `SelectedTab` once. Keep batch helper in ViewModel to avoid N tail restarts.
4. **Path case and normalization:** dedup is case-insensitive today. Normalizing to full path before send plus existing `FindTabByPath` keeps this consistent. Symlink aliasing remains a known duplicate edge, accepted.
5. **Pipe name collision per user:** global name could collide on shared machines. Mitigate by suffixing with user SID or hash on Windows, UID on Unix, if qa flags it. Default keeps simple constant for now.

## Implementation Order

1. UI: `CliArgs` record + `CliArgsParser` with expansion (pure + IO isolated for tests).
2. UI: `StartupFiles` holder + `Program.Main` parse, help/version early exit, mutex check.
3. UI: `SingleInstanceClient` send with timeout + fallback to owner.
4. UI: `SingleInstanceServer` loop in `App`, UI-thread dispatch to `AddTab`, activation.
5. UI: batch open helper in `MainWindowViewModel` to avoid N tail restarts on bulk open.
6. Tests for parser, payload, ViewModel dedup, plus manual smoke above.
7. Docs: update README usage with new CLI surface.
