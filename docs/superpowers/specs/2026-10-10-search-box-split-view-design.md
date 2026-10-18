# Search Box with Split Results View

**Date:** 2026-10-10 (revised 2026-10-10: scope narrowed to active tab with an All-tabs toggle)
**Status:** Implemented
**Scope:** UI only (`ViewModels`, `MainWindow.axaml`, `MainWindow.axaml.cs`, `tests/LogTail.UI.Tests`)

## 1. Goal

Implements the search/filter box that was explicitly deferred in the workspace-sidebar spec (§7 Non-Goals).

A search box in the top toolbar searches the **log on screen** by default. Matches are listed in a **resizable panel at the bottom** of the log area. A toggle in the panel header extends the search to **all open tabs**; in that mode each result row carries its file name and clicking one switches to that tab. Clicking a result scrolls the log view to that line. `Esc` or clearing the text closes the panel.

Tail/buffer/session logic is untouched — this is a read-only view over the `LogEvents` the tabs already hold.

## 2. UX

### 2.1 Layout

```
┌──────────────────────────────────────────────────────────────┐
│ File         [ Find in logs…            (Ctrl+F) ]      ⚙    │ ← toolbar
├──────────────────────────────────────────────────────────────┤
│ TabControl (existing log view, unchanged)                    │
│                                                              │
├──────────────────────────────────────────────────────╫────────┤ ← GridSplitter (drag)
│ 12.345 results   [✓] All tabs                          [×]                             │ ← results panel
│ error.log  1042 · NullReferenceException at Foo.Bar()        │
│ app.log      88 · WARN payment timeout, retrying             │
└──────────────────────────────────────────────────────────────┘
 (status bar below the panel; gains the match count while a query is active)
```

- Search box sits at the right end of the toolbar, just left of the settings button — fixed 240px, no stretch. Watermark "Find in logs… (Ctrl+F)".
- Results panel docks above the status bar. Default height 180px; the GridSplitter above it drags between 80px and the rest of the window. The resized height is kept for the rest of the session; a restart returns to 180px.
- `All tabs` toggle (panel header, default **off**): off → search only the log on screen; on → every open tab, results labeled by file.
- Empty query → panel and splitter hidden, no space reserved (row height 0).
- While a query is active, the log area's status line appends the same count (`… | N results`) — visible even with the panel closed.
- Closing the panel (`×` / `Esc`) hides it but **keeps the query** — `Ctrl+F` or the next keystroke reopens the same results.

### 2.2 Interactions

| Action | Result |
|---|---|
| Type in the search box | Debounce 300ms → matches listed; count shown in the header |
| Toggle `All tabs` | Re-search with the new scope: active log only (off) or every open tab (on) |
| Switch tabs while a query is open | Results follow the tab on screen (off mode); unchanged in on mode |
| Click a result row | Off mode: scroll that line on screen. On mode: `SelectedTab = match.Tab`, then scroll |
| `Enter` in the search box | Jump to the first result |
| `×` / `Esc` | Panel closes, **query stays**; `Ctrl+F` or typing reopens it |
| Clear the text | Panel closes; typing a query opens it again |
| `Ctrl+F` anywhere | Focus the search box and reopen the panel |
| Click / focus the search box | Reopens the panel (query still present) |

### 2.3 Result row

`[file name]` (Auto width, capped 150px, ellipsized) · line number (~55px, right-aligned) · line text (ellipsized, full line as tooltip). The file column is always shown: in off mode it names the file on screen, in on mode the source of each match.

## 3. Components & Data Flow

### 3.1 SearchViewModel (new file)

Owned by `MainWindowViewModel`. Constructor takes `ObservableCollection<TabViewModel> tabs` and `Action<TabViewModel> activateTab` — no reference back to the parent, so the VM stays testable without Avalonia.

- `SearchText` (string) — TwoWay bound to the TextBox. Avalonia's `TextBox.Text` default `UpdateSourceTrigger` is `PropertyChanged`, so every keystroke reaches the VM; the VM debounces 300ms before scanning.
- `SearchAllTabs` (bool, default false) — the scope toggle.
- `SelectedTab` (TabViewModel?) + `SetActiveTab(TabViewModel?)` — the parent pushes the currently selected tab whenever it changes (including a folder tab swapping its preview). A push re-triggers the debounced scan.
- Scan: substring match with `StringComparison.OrdinalIgnoreCase`. No result cap — the count is the real total and the list holds every match (each match is a small record holding a tab reference and the event it points at; the scan's cost is the line walk either way; the ListBox virtualizes so only visible rows render).
  - **Off**: search `SelectedTab?.LogSource.LogEvents` only (so a folder tab searches its previewed file); matches carry `Tab = SelectedTab`, so a click never switches tabs.
  - **On**: iterate `tabs` in order; per tab iterate `t.LogSource.LogEvents`; matches carry that tab.
- The scan runs on `Task.Run`; `Matches` (a `BulkObservableCollection<LogSearchMatch>` cleared and refilled via `AddRange` — one UI notification per search) and `MatchCount` land back on the UI thread. A generation counter drops a scan superseded by a newer one.
- Re-scans on: `SearchText` (debounced 300ms), `SearchAllTabs` change, `SetActiveTab` change, `tabs.CollectionChanged` (tab opened/closed).
- `Matches`, `MatchCount`, `MatchCountText` (`"N results"` / `"No matches"`), `SelectedMatch`, `HasQuery` (trimmed non-empty), `IsPanelOpen`/`ShowPanel` (panel visibility), `OpenPanel()`/`ClosePanel()`, `PanelHeight`.
- `PanelHeight` is a **`GridLength`** (Avalonia.Controls), not a `double`: the results `RowDefinition.Height` then binds with no converter, and the GridSplitter's `DragCompleted` hook stores the resized length. Serves `0` while the panel is hidden (no query, or closed — the row reserves no space) and the last dragged height otherwise.
- `JumpToMatchCommand` / `JumpToMatch(match)`: if `match.Tab.LogSource.LogEvents` still contains `match.Event`, call `activateTab(match.Tab)` (a no-op when it is already the selected tab) then handle the `ScrollToLine` interaction so the View scrolls. A line evicted from the ring buffer (or a tab closed) means no jump.
- `CloseCommand` / `ClosePanel()`: hides the panel without touching the query. `OpenPanel()` reopens it (`ShowPanel` = query present **and** panel open).
- `Interaction<LogSearchMatch, bool> ScrollToLine` — the View's scroll hook (documented ReactiveUI/Avalonia pattern, same style as the existing `ShowOpenFileDialog`).

### 3.2 LogSearchMatch (new file)

```csharp
public sealed record LogSearchMatch(TabViewModel Tab, EnrichedLogEvent Event)
{
    public string FileName => Tab.FileName;
    public int LineNumber => Event.LineNumber;
    public string Line => Event.Raw.Line;
}
```

### 3.3 MainWindowViewModel

Three small additions, keeping the (deferred) god-module refactor at bay:

```csharp
public SearchViewModel Search { get; }
// ctor (before any tab restore):
Search = new SearchViewModel(Tabs, tab => SelectedTab = tab);
```

```csharp
private void SyncActiveLogTab()
{
    ActiveLogTab = SelectedTab is { IsFolder: true } folder ? folder.Preview : SelectedTab;
    Search?.SetActiveTab(SelectedTab);   // preview swaps also route through this method
}
```

`SyncActiveLogTab` already runs on every `SelectedTab` change and on every preview swap (`ShowWorkspaceFileAsync`, `SwitchLogTabToFileAsync`), so the search's scope tab stays in sync without a new subscription. The null-conditional guards ctor ordering.

### 3.4 MainWindow.axaml

- Toolbar Grid becomes `ColumnDefinitions="Auto,*,Auto"`: Menu | `TextBox x:Name="SearchTextBox"` fixed 240px right-aligned (`Text="{Binding Search.SearchText}"`, Watermark "Find in logs… (Ctrl+F)") | settings button (unchanged).
- The TabControl's fill slot inside the content DockPanel becomes a Grid:

```xml
<Grid>
  <Grid.RowDefinitions>
    <RowDefinition Height="*" />
    <RowDefinition Height="Auto" />
    <RowDefinition Height="{Binding Search.PanelHeight, Mode=TwoWay}" />
  </Grid.RowDefinitions>

  <TabControl Grid.Row="0" ... />   <!-- existing markup, moved verbatim -->

  <GridSplitter Grid.Row="1" Height="4" ResizeDirection="Rows"
                IsVisible="{Binding Search.HasQuery}"
                Background="{DynamicResource SystemControlForegroundBaseLowBrush}" />

  <Border Grid.Row="2" IsVisible="{Binding Search.HasQuery}"
          BorderBrush="{DynamicResource SystemControlForegroundBaseLowBrush}"
          BorderThickness="0,1,0,0">
    <Grid RowDefinitions="Auto,*">
      <Grid Grid.Row="0" ColumnDefinitions="Auto,*,Auto" Margin="8,4">
        <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
          <TextBlock Text="{Binding Search.MatchCountText}" FontSize="11" VerticalAlignment="Center" />
          <ToggleSwitch IsChecked="{Binding Search.SearchAllTabs, Mode=TwoWay}" FontSize="10" />
          <TextBlock Text="All tabs" FontSize="11" Opacity="0.7" VerticalAlignment="Center" />
        </StackPanel>
        <Button Grid.Column="2" Content="×" Command="{Binding Search.CloseCommand}"
                Background="Transparent" BorderThickness="0" ToolTip.Tip="Close (Esc)" />
      </Grid>
      <ListBox Grid.Row="1" ItemsSource="{Binding Search.Matches}"
               SelectedItem="{Binding Search.SelectedMatch, Mode=TwoWay}"
               DoubleTapped="OnSearchResultActivated"
               ScrollViewer.HorizontalScrollBarVisibility="Disabled">
        <ListBox.ItemTemplate>
          <DataTemplate x:DataType="vm:LogSearchMatch">
            <Grid ColumnDefinitions="Auto,55,*" MinHeight="20" VerticalAlignment="Center">
              <TextBlock Grid.Column="0" Text="{Binding FileName}" MaxWidth="150"
                         TextTrimming="CharacterEllipsis" FontSize="11" Opacity="0.7"
                         VerticalAlignment="Center" />
              <TextBlock Grid.Column="1" Text="{Binding LineNumber}" TextAlignment="Right"
                         FontSize="11" Opacity="0.5" VerticalAlignment="Center" Margin="0,0,8,0" />
              <TextBlock Grid.Column="2" Text="{Binding Line}" TextTrimming="CharacterEllipsis"
                         FontFamily="Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace"
                         FontSize="12" VerticalAlignment="Center"
                         ToolTip.Tip="{Binding Line}" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </Grid>
  </Border>
</Grid>
```

`CloseCommand` hides the panel (`ShowPanel`), leaving the query intact. Existing window styles and the empty-state/loading overlays are untouched.

### 3.5 MainWindow.axaml.cs

- `WhenActivated`: register the `ScrollToLine` interaction handler (same registration style as `ShowOpenFileDialog`). The handler posts to `DispatcherPriority.Loaded` — a cross-tab jump needs the newly selected tab's content host to exist first — then:
  1. find the on-screen content host's `ScrollViewer` with the existing lookup (`FindControl<TabControl>("MainTabControl")?.FindDescendantOfType<ScrollViewer>()`);
  2. `index = match.Tab.LogSource.LogEvents.IndexOf(match.Event)`; `-1` → set output false and ignore;
  3. `ContainerFromIndex(index)?.BringIntoView()` when the container is realized; otherwise the uniform-height fallback (every log row is the same 20px, `TextWrapping="NoWrap"`): `rowHeight = Extent.Height / LogEvents.Count`, `Offset = new Vector(Offset.X, clamp(index × rowHeight − Viewport.Height / 2, 0, Extent.Height − Viewport.Height))`, inside the existing `_isProgrammaticScroll` guard so AutoScroll is not disturbed.
- Window `KeyDown`: `Ctrl+F` → focus `SearchTextBox`; `Enter` while the search box is focused → `Search.JumpToMatch(first match)`; `Esc` while the search box is focused → clear `Search.SearchText`.
- `OnSearchResultActivated` (ListBox `DoubleTapped`) → `ViewModel.Search.JumpToMatch(...)` with the clicked match.

## 4. Search Semantics

- Plain substring, case-insensitive, no regex (v1).
- **Off (default)**: the log on screen — `SelectedTab.LogSource`, so a folder tab searches the file previewed inside it. All matches belong to the tab on screen; a click only scrolls.
- **On**: every entry of `Tabs`, in tab order, via `t.LogSource`; a click on a result from another tab switches to it and scrolls.
- Result order: tab order (on mode) then `LineNumber` ascending.
- Re-search when the query, the scope toggle, the selected tab, or the tab list changes — one debounce (300ms) in front of all of them, generation counter drops stale scans.
- Scope is session-only; a restart returns to off.
- The same file can be open both as a log tab and previewed inside a folder tab; its lines then appear twice in on mode. Acceptable for v1.

## 5. Edge Cases

- No matches → panel stays open showing `No matches`; the status line shows `| No matches`.
- Common short query (e.g. `e`) → every match is collected; the header shows the real total (tens of thousands) and the ListBox virtualizes, so scrolling renders only visible rows.
- Switching tabs (or previewing another file in a folder tab) with a query open → off-mode results follow the tab on screen.
- Match line evicted from the ring buffer, or its tab closed, before the click → `JumpToMatch` does nothing (checked before switching tabs).
- All tabs closed → empty results, panel hidden.
- Rapid typing / tab churn → debounce plus a generation counter; a stale scan's results never land.
- Panel height dragged → kept in the VM for the session; restart resets to 180px.

## 6. Tests

`tests/LogTail.UI.Tests/ViewModels/SearchViewModelTests.cs` (xUnit + FluentAssertions, plain VM — no Avalonia host needed):

- empty or whitespace query → no matches, `PanelHeight` 0;
- `ClosePanel` keeps `SearchText`; the next keystroke reopens the panel;
- off mode: a query present in two open tabs returns only the active tab's lines, in line order;
- `SetActiveTab` to another tab → results follow the new tab;
- on mode: results collected from every open tab in tab order, then line order; toggling back to off narrows to the active tab;
- matching is case-insensitive;
- a folder tab's preview lines are searched in off mode and the match points at the folder tab;
- `MatchCount`/`MatchCountText` correct, including a count above the old 10,000 cap (no cap anymore — real total, full list);
- `JumpToMatch` with a present line → activates the right tab (captured action) and fires the `ScrollToLine` interaction;
- `JumpToMatch` with an evicted line → does nothing.

`tests/LogTail.UI.Tests/Views/SearchPanelVisibilityTests.cs` (`[AvaloniaFact]` per project convention — anything touching the dispatcher):

- setting a query on a constructed window's ViewModel shows the panel; clearing hides it.

`tests/LogTail.UI.Tests/MainWindowViewModelSearchStatusTests.cs` (`[AvaloniaFact]` — drives real tailing into the VM):

- with a query active, the status line carries the match count, including with the results panel closed;
- clearing the query drops the count from the status line.

Not unit tested: debounce timing, the scroll interaction handler, and keyboard shortcuts — verified manually per §8. Test execution follows repo preference: only the touched projects, not the full suite.

## 7. Non-Goals (v1)

- Auto-refreshing results while new tailing lines arrive (results refresh on the next keystroke, toggle, or tab switch).
- Regex, case-sensitivity toggle, highlighting matches in the main log view.
- Persisting panel height or the `All tabs` toggle into `Settings`.
- Search history; per-row actions (copy line, open containing folder).

## 8. Verification

1. Build the solution; run `tests/LogTail.UI.Tests` (and `tests/LogTail.Core.Tests` only if shared code moves — it should not).
2. Per repo rule for desktop changes: build and run `LogTail.exe`, then hand the app to the user to verify manually before calling it done.
3. Manual checks: open 2+ log files; with `All tabs` off, type a word present in both → only the on-screen file's lines; switch tabs → results follow; toggle `All tabs` → both files listed with file labels; click a result from the other tab → tab switches and the line is on screen; drag the splitter → panel resizes; `×` and `Esc` close; `Ctrl+F` focuses the box; a query with no matches shows `No matches`.
