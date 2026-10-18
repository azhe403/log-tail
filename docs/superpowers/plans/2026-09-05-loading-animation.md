# Loading Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display a visual loading overlay with an animated spinner and status text when opening a large log file until the initial load completes.

**Architecture:** Add `IsLoading` and `LoadingMessage` state to `TabViewModel`, manage the loading lifecycle in `MainWindowViewModel.StartTailingAsync` (active when starting, inactive when `InitialLogLoaded` finishes flushing or an error occurs), integrate the NuGet `LoadingIndicators.Avalonia` package into `LogTail.UI`, and slot `LoadingOverlay` above the per-tab `ListBox` in `MainWindow.axaml`.

**Tech Stack:** C# 12 / .NET 10, Avalonia 11/12, ReactiveUI, LoadingIndicators.Avalonia, xUnit, FluentAssertions.

## Global Constraints

- Per project memory and convention: Always use `--artifacts-path .artifacts-test` when running `dotnet test` or `dotnet build` to prevent file locks on the UI DLLs.
- Mind C# nullability (`<Nullable>enable</Nullable>`).
- Do not change the existing incremental streaming/tailing performance contract.

---

### Task 1: Add Loading Properties to TabViewModel with Unit Tests

**Files:**
- Modify: `src/LogTail.UI/ViewModels/TabViewModel.cs`
- Modify: `tests/LogTail.UI.Tests/ViewModels/TabViewModelTests.cs`

**Interfaces:**
- Consumes: `ReactiveObject`, `RaiseAndSetIfChanged`
- Produces: `bool IsLoading { get; set; }`, `string LoadingMessage { get; set; }`

- [ ] **Step 1: Write the failing test**

Add unit tests to `tests/LogTail.UI.Tests/ViewModels/TabViewModelTests.cs`:
```csharp
[Fact]
public void Constructor_WhenInitialized_HasDefaultLoadingProperties()
{
    var path = Path.Combine(Path.GetTempPath(), "test.log");
    File.WriteAllText(path, "content");
    var sut = new TabViewModel(path);

    sut.IsLoading.Should().BeFalse();
    sut.LoadingMessage.Should().Be("Reading log file...");
}

[Fact]
public void IsLoading_WhenSet_NotifiesPropertyChanged()
{
    var path = Path.Combine(Path.GetTempPath(), "test.log");
    File.WriteAllText(path, "content");
    var sut = new TabViewModel(path);
    var propertyChangedRaised = false;
    sut.PropertyChanged += (s, e) =>
    {
        if (e.PropertyName == nameof(TabViewModel.IsLoading))
        {
            propertyChangedRaised = true;
        }
    };

    sut.IsLoading = true;

    sut.IsLoading.Should().BeTrue();
    propertyChangedRaised.Should().BeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --artifacts-path .artifacts-test --filter "FullyQualifiedName~TabViewModelTests"`
Expected: FAIL (Compilation error: `IsLoading` / `LoadingMessage` not found).

- [ ] **Step 3: Implement minimal code in TabViewModel.cs**

In `src/LogTail.UI/ViewModels/TabViewModel.cs`:
```csharp
private bool _isLoading;
private string _loadingMessage = "Reading log file...";

public bool IsLoading
{
    get => _isLoading;
    set => this.RaiseAndSetIfChanged(ref _isLoading, value);
}

public string LoadingMessage
{
    get => _loadingMessage;
    set => this.RaiseAndSetIfChanged(ref _loadingMessage, value);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --artifacts-path .artifacts-test --filter "FullyQualifiedName~TabViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LogTail.UI/ViewModels/TabViewModel.cs tests/LogTail.UI.Tests/ViewModels/TabViewModelTests.cs
git commit -m "feat(ui): add IsLoading and LoadingMessage properties to TabViewModel"
```

---

### Task 2: Manage Loading State Lifecycle in MainWindowViewModel

**Files:**
- Modify: `src/LogTail.UI/ViewModels/MainWindowViewModel.cs`
- Modify: `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `TabViewModel.IsLoading`, `ILogSource.InitialLogLoaded`, `source.Events`
- Produces: `StartTailingAsync` sets `IsLoading = true`, `OnInitialLogLoaded` and `onError` set `IsLoading = false`

- [ ] **Step 1: Write the failing tests**

Add tests to `tests/LogTail.UI.Tests/MainWindowViewModelTests.cs`:
```csharp
[Fact]
public async Task StartTailingAsync_WhenInitialLoadCompletes_SetsIsLoadingFalse()
{
    var sut = CreateViewModel();
    var path = CreateLogFile("large.log");
    File.WriteAllLines(path, Enumerable.Range(1, 10).Select(i => $"Line {i}"));

    var tab = new TabViewModel(path);
    sut.Tabs.Add(tab);
    sut.SelectedTab = tab;

    // Trigger tailing via selection / command
    await Task.Delay(200);

    tab.IsLoading.Should().BeFalse();
}
```

- [ ] **Step 2: Run test to verify behavior**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --artifacts-path .artifacts-test --filter "FullyQualifiedName~StartTailingAsync_WhenInitialLoadCompletes_SetsIsLoadingFalse"`

- [ ] **Step 3: Implement loading lifecycle in MainWindowViewModel.cs**

Update `StartTailingAsync` in `src/LogTail.UI/ViewModels/MainWindowViewModel.cs`:
```csharp
tab.IsLoading = true;
tab.LoadingMessage = "Reading log file...";

// In OnInitialLogLoaded handler:
void OnInitialLogLoaded()
{
    // ... existing flush logic ...
    tab.IsLoading = false;
}

// In onError handler:
onError: ex =>
{
    tab.IsTailing = false;
    tab.IsLoading = false;
    tab.Status = "error";
    StatusMessage = $"Error: {ex.Message}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogTail.UI.Tests/LogTail.UI.Tests.csproj --artifacts-path .artifacts-test`
Expected: ALL PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LogTail.UI/ViewModels/MainWindowViewModel.cs tests/LogTail.UI.Tests/MainWindowViewModelTests.cs
git commit -m "feat(ui): manage tab IsLoading lifecycle during initial log read"
```

---

### Task 3: Add LoadingIndicators.Avalonia Package and Register Theme Styles

**Files:**
- Modify: `src/LogTail.UI/LogTail.UI.csproj`
- Modify: `src/LogTail.UI/App.axaml`

**Interfaces:**
- Consumes: NuGet `LoadingIndicators.Avalonia`
- Produces: Registered `LoadingIndicators.axaml` style resource in `App.axaml`

- [ ] **Step 1: Add LoadingIndicators.Avalonia NuGet package to LogTail.UI.csproj**

Add package reference:
```xml
<PackageReference Include="LoadingIndicators.Avalonia" Version="11.0.1" />
```

- [ ] **Step 2: Include style in App.axaml**

In `src/LogTail.UI/App.axaml`:
```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://LoadingIndicators.Avalonia/LoadingIndicators.axaml" />
</Application.Styles>
```

- [ ] **Step 3: Build to verify package restore and XAML compilation**

Run: `dotnet build src/LogTail.UI/LogTail.UI.csproj --artifacts-path .artifacts-test`
Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/LogTail.UI/LogTail.UI.csproj src/LogTail.UI/App.axaml
git commit -m "build(ui): add LoadingIndicators.Avalonia dependency and style inclusion"
```

---

### Task 4: Create LoadingOverlay View and Integrate in MainWindow.axaml

**Files:**
- Create: `src/LogTail.UI/Views/LoadingOverlay.axaml`
- Create: `src/LogTail.UI/Views/LoadingOverlay.axaml.cs`
- Modify: `src/LogTail.UI/Views/MainWindow.axaml`

**Interfaces:**
- Consumes: `TabViewModel.IsLoading`, `TabViewModel.LoadingMessage`, `li:LoadingIndicator`
- Produces: Visual overlay in TabContent displaying animated spinner + dynamic text when `IsLoading == true`

- [ ] **Step 1: Create LoadingOverlay.axaml**

Create `src/LogTail.UI/Views/LoadingOverlay.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:LogTail.UI.ViewModels"
             xmlns:li="using:LoadingIndicators.Avalonia;assembly=LoadingIndicators.Avalonia"
             x:Class="LogTail.UI.Views.LoadingOverlay"
             x:DataType="vm:TabViewModel">
  <Border IsVisible="{Binding IsLoading}"
          Background="#66000000"
          HorizontalAlignment="Stretch"
          VerticalAlignment="Stretch"
          IsHitTestVisible="True">
    <StackPanel HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Spacing="16">
      <li:LoadingIndicator Mode="Arc"
                           SpeedRatio="1.2"
                           Width="48"
                           Height="48"
                           IsActive="{Binding IsLoading}"
                           Foreground="{DynamicResource SystemAccentColorBrush}" />
      <TextBlock Text="{Binding LoadingMessage}"
                 FontSize="13"
                 Foreground="{DynamicResource SystemControlForegroundBaseHighBrush}"
                 HorizontalAlignment="Center" />
    </StackPanel>
  </Border>
</UserControl>
```

- [ ] **Step 2: Create LoadingOverlay.axaml.cs**

Create `src/LogTail.UI/Views/LoadingOverlay.axaml.cs`:
```csharp
using Avalonia.Controls;

namespace LogTail.UI.Views;

public partial class LoadingOverlay : UserControl
{
    public LoadingOverlay()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 3: Integrate LoadingOverlay into MainWindow.axaml TabControl.ContentTemplate**

In `src/LogTail.UI/Views/MainWindow.axaml`:
```xml
      <TabControl.ContentTemplate>
        <DataTemplate x:DataType="vm:TabViewModel">
          <Grid ColumnDefinitions="*" HorizontalAlignment="Stretch">
            <ListBox ItemsSource="{Binding LogEvents}"
                     SelectionMode="Multiple"
                     Name="TabLogListBox"
                     Grid.Column="0"
                     HorizontalAlignment="Stretch"
                     PointerPressed="OnTabLogPointerPressed"
                     ScrollViewer.HorizontalScrollBarVisibility="Auto"
                     ScrollViewer.IsDeferredScrollingEnabled="True">
              <!-- existing ListBox content -->
            </ListBox>
            <views:LoadingOverlay Grid.Column="0" />
          </Grid>
        </DataTemplate>
      </TabControl.ContentTemplate>
```

- [ ] **Step 4: Build and run test suite**

Run: `dotnet test --artifacts-path .artifacts-test`
Expected: PASS with all tests green.

- [ ] **Step 5: Commit**

```bash
git add src/LogTail.UI/Views/LoadingOverlay.axaml src/LogTail.UI/Views/LoadingOverlay.axaml.cs src/LogTail.UI/Views/MainWindow.axaml
git commit -m "feat(ui): add LoadingOverlay view with Arc indicator into MainWindow tab template"
```

---

### Task 5: Final Validation & Regression Testing

**Files:**
- Test: All test suites in `tests/`

- [ ] **Step 1: Run complete test suite**

Run: `dotnet test --artifacts-path .artifacts-test`
Expected: 100% tests passing across Core and UI test projects.

- [ ] **Step 2: Verify git hygiene**

Run: `git status`
Verify no untracked artifacts or absolute paths leaked.
