# LogTail Drag & Drop Feature Design

**Date**: 2026-09-03  
**Milestone**: 1  
**Status**: Implemented  

## Overview

Add comprehensive drag and drop functionality to LogTail, enabling users to:
1. Drop log files from the OS file manager into the application
2. Drag log lines out to external applications

This feature also introduces a tab system to manage multiple open log files.

## Goals

- Allow users to open log files by dragging them from Explorer/Finder/file manager
- Allow users to drag log lines to external apps (Notepad, VS Code, etc.)
- Support multiple open files via a tabbed interface
- Maintain minimalist, fast, native feel

## Non-Goals

- Network file streaming (Milestone 2)
- Drag and drop of folders (files only)
- Custom drag animations or effects

---

## Architecture

### Approach Selection

**Chosen: Avalonia Native (TabControl + built-in DragDrop)**

| Approach | Pros | Cons |
|----------|------|------|
| Avalonia Native | No dependencies, stable API, minimal code | Basic tab styling |
| Custom implementation | Full control, unique look | More code, more bugs |
| Third-party library | Rich features | External dependency |

### New Files

| File | Purpose |
|------|---------|
| `ViewModels/TabViewModel.cs` | Per-tab state (file path, log events, status) |
| `Views/TabItemView.axaml` | Custom tab header with close button |

### Modified Files

| File | Changes |
|------|---------|
| `ViewModels/MainWindowViewModel.cs` | Add `ObservableCollection<TabViewModel>`, `SelectedTab`, `AddTab()`, `CloseTab()` |
| `Views/MainWindow.axaml` | Replace `ListBox` with `TabControl`, add `DragDrop.AllowDrop`, status bar updates |
| `Views/MainWindow.axaml.cs` | Add `DragDrop.Drop` handler, `DragDrop.DragOver` handler |

---

## Section 1: Tab System Architecture

### ViewModel Changes

```csharp
// MainWindowViewModel.cs
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<TabViewModel> _tabs = new();
    
    [ObservableProperty]
    private TabViewModel? _selectedTab;
    
    public ICommand OpenFileCommand { get; }
    public ICommand CloseTabCommand { get; }
    
    public void AddTab(string filePath);
    public void CloseTab(TabViewModel tab);
    public TabViewModel? FindTabByPath(string filePath);
}
```

```csharp
// TabViewModel.cs
public partial class TabViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;
    
    [ObservableProperty]
    private string _filePath = string.Empty;
    
    [ObservableProperty]
    private string _status = "Idle";
    
    [ObservableProperty]
    private ObservableCollection<LogEvent> _logEvents = new();
    
    [ObservableProperty]
    private int _lineCount;
    
    [ObservableProperty]
    private long _fileSize;
    
    [ObservableProperty]
    private DateTime _lastModified;
    
    [ObservableProperty]
    private double _linesPerSecond;
    
    [ObservableProperty]
    private bool _isTailing;
}
```

### UI Changes

```xml
<!-- MainWindow.axaml -->
<TabControl ItemsSource="{Binding Tabs}"
            SelectedItem="{Binding SelectedTab}">
    <TabControl.ItemTemplate>
        <DataTemplate>
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding FileName}" />
                <Button Command="{Binding $parent[TabControl].((MainWindowViewModel)DataContext).CloseTabCommand}"
                        CommandParameter="{Binding}"
                        Content="X" />
            </StackPanel>
        </DataTemplate>
    </TabControl.ItemTemplate>
    
    <TabControl.ContentTemplate>
        <DataTemplate>
            <ListBox ItemsSource="{Binding LogEvents}" />
        </DataTemplate>
    </TabControl.ContentTemplate>
</TabControl>
```

---

## Section 2: Drop Files from Explorer

### Behavior

- User drags files from OS file manager → drops anywhere on LogTail window
- Each dropped file opens in a new tab
- If file already open in another tab, focus that tab (no duplicate)
- Dropped folders are ignored
- Only `.log` and `.txt` files are accepted

### Implementation

```xml
<!-- MainWindow.axaml -->
<Window DragDrop.AllowDrop="True"
        DragDrop.Drop="OnDrop"
        DragDrop.DragOver="OnDragOver">
```

```csharp
// MainWindow.axaml.cs
private void OnDragOver(object? sender, DragEventArgs e)
{
    e.DragEffects = e.Data.Contains(DataFormats.Files) 
        ? DragDropEffects.Copy 
        : DragDropEffects.None;
}

private void OnDrop(object? sender, DragEventArgs e)
{
    if (e.Data.Contains(DataFormats.Files))
    {
        var files = e.Data.GetFiles();
        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (IsValidLogFile(path))
            {
                ViewModel.AddTab(path);
            }
        }
    }
}

private bool IsValidLogFile(string path)
{
    if (!File.Exists(path)) return false;
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext is ".log" or ".txt";
}
```

### Validation

- Check file exists before opening
- Handle permission error (file locked, no read access)
- Status bar shows error message on failure

---

## Section 3: Drag Log Lines Out

### Behavior

- User can drag log lines from ListBox to external apps
- Dragged text is the raw log line content
- Multi-select support: drag all selected lines
- Visual feedback: cursor changes on drag start

### Implementation

```xml
<!-- TabItemView.axaml or MainWindow.axaml ListBox -->
<ListBox SelectionMode="Extended"
         DragDrop.IsDragEnabled="True">
    <ListBox.ItemContainerTheme>
        <ControlTheme TargetType="ListBoxItem">
            <Setter Property="DragDrop.IsDragEnabled" Value="True" />
        </ControlTheme>
    </ListBox.ItemContainerTheme>
</ListBox>
```

```csharp
// Handle drag start - set text data
private void OnLogLineDrag(object sender, DragEventArgs e)
{
    var selectedItems = listBox.SelectedItems;
    if (selectedItems.Count == 0)
    {
        e.DragEffects = DragDropEffects.None;
        return;
    }
    
    var text = string.Join(Environment.NewLine, 
        selectedItems.Cast<LogEvent>().Select(x => x.Message));
    
    e.Data.Set(DataFormats.Text, text);
    e.DragEffects = DragDropEffects.Copy;
}
```

### Edge Cases

- Empty selection: ignore drag (no effect)
- Long lines: full text copied, display truncated
- Special characters: properly escaped in plain text format

---

## Section 4: Visual Feedback & Polish

### Line Numbers

- Display line number at the start of each log line
- Format: `{lineNumber}. {message}` (number + dot + space + message)
- Fixed-width alignment for line numbers (prevents shifting)
- Same monospace font as log message

```xml
<!-- Log line template -->
<StackPanel Orientation="Horizontal">
    <TextBlock Text="{Binding LineNumber}"
               Width="50"
               TextAlignment="Right"
               Margin="0,0,8,0" />
    <TextBlock Text="{Binding Message}" />
</StackPanel>
```

### Drop Zone Feedback

- During drag: highlight border or show overlay "Drop files here"
- On drag leave: remove indicator

```xml
<Border Classes="drop-zone"
        DragDrop.AllowDrop="True">
    <!-- Content -->
</Border>

<!-- In Styles.axaml -->
<Style Selector="Border.drop-zone:pointerover">
    <Setter Property="BorderBrush" Value="{DynamicResource AccentColor}" />
    <Setter Property="BorderThickness" Value="2" />
</Style>
```

### Tab Close Behavior

- Close button (X) on each tab header
- Click X to close tab
- Last tab closed → show empty state

### Empty State

When no tabs are open, display centered message:
```
Drop a log file here
or use File > Open
```

### Status Bar Updates

Display comprehensive information about the active tab:

```
[filename.log] C:\full\path\to\file.log | 2.5 MB | Modified: 2026-09-03 15:30 | 125 lines/s | Tailing
```

| Field | Description |
|-------|-------------|
| Filename | Display name of active tab file |
| Full Path | Complete file path |
| File Size | Human-readable format (KB, MB, GB) |
| Last Modified | Timestamp of last file change |
| Lines/Second | Current tailing speed |
| Status | `Tailing` or `Paused` |

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| File not found | Show error in status bar, don't open tab |
| Permission denied | Show error: "Cannot read file: access denied" |
| File locked | Show error: "File is locked by another process" |
| Invalid file type | Silently ignore (filtered before tab creation) |
| Folder dropped | Silently ignore |

---

## Testing Strategy

1. **Unit Tests**
   - `TabViewModel` state management
   - `MainWindowViewModel.AddTab()` deduplication
   - `MainWindowViewModel.CloseTab()` cleanup

2. **Integration Tests**
   - Drop file → tab created
   - Drop same file → no duplicate tab
   - Close tab → resources freed
   - Drag log line → text data set correctly

3. **Manual Testing**
   - Cross-platform drag from Explorer/Finder
   - Drop multiple files at once
   - Drag to VS Code, Notepad, terminal
   - Status bar accuracy

---

## Implementation Order

1. Tab system (ViewModel + UI)
2. Drop files from Explorer
3. Drag log lines out
4. Visual feedback (line numbers, drop zone, empty state)
5. Status bar updates
6. Error handling
7. Testing

---

## Decision Log

| Decision | Rationale |
|----------|-----------|
| Avalonia Native over custom | Minimal dependencies at Milestone 1 |
| Filter .log/.txt only | User preference for log-focused tool |
| Tab per file | Most intuitive multi-file UX |
| Line numbers with dot separator | User preference |
| Status bar with full metadata | Comprehensive monitoring capability |
