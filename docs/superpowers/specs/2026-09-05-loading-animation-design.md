# Loading Animation with LoadingIndicators.Avalonia Design

**Date:** 2026-09-05
**Status:** Implemented
**Scope:** UI (`LogTail.UI`) + UI Tests (`LogTail.UI.Tests`)

## Problem

When opening a large log file (100k+ lines), the initial load is processed asynchronously and buffered in the background until `InitialLogLoaded` is triggered (as specified in the initial load specification). However, while the initial reading process is running, the log area on the tab appears empty and relies only on a small status bar without a clear visual indicator that the file is being processed. This gives the impression that the app is unresponsive or that the process has stopped.

## Goal

Provide clear and aesthetic visual feedback while the initial loading of the log file is in progress by:
1. Displaying a semi-transparent overlay over the `ListBox` area of each tab when the tab is in the loading state (`IsLoading = true`).
2. Using the `LoadingIndicator` spinner component from the `LoadingIndicators.Avalonia` NuGet package with a minimalist `Arc` / `Ring` style that adapts to the active theme (Light, Dark, System).
3. Displaying a dynamic status message ("Reading file...") that reinforces the sense of responsiveness.
4. Removing the overlay automatically once `InitialLogLoaded` has finished flushing to the UI.

## Architecture & Design

### 1. Dependencies (`LogTail.UI.csproj`)

Add the NuGet package:
- `LoadingIndicators.Avalonia` (latest version compatible with Avalonia 11)

Register the style in `App.axaml`:
```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://LoadingIndicators.Avalonia/LoadingIndicators.axaml" />
</Application.Styles>
```

### 2. ViewModel Layer

#### `TabViewModel` (`src/LogTail.UI/ViewModels/TabViewModel.cs`)
Add observable properties:
- `bool IsLoading`: Controls the visibility of the loading overlay (default `false`).
- `string LoadingMessage`: The text message displayed below the spinner (default `"Reading log file..."`).

#### `MainWindowViewModel` (`src/LogTail.UI/ViewModels/MainWindowViewModel.cs`)
1. In `StartTailingAsync(TabViewModel tab)`:
   - Before starting `source.StartAsync()`, set `tab.IsLoading = true` and `tab.LoadingMessage = "Reading log file..."`.
   - Configure the `OnInitialLogLoaded` handler: when the event fires and the buffer is flushed, set `tab.IsLoading = false`.
   - If a file reading error occurs (`onError`), set `tab.IsLoading = false`.
2. In `StopCurrentSourceAsync()` / when switching tabs:
   - Ensure the tab that is being left behind or stopped has `IsLoading = false` if it is no longer actively loading.

### 3. View Layer

#### `TabLoadingOverlay` (`src/LogTail.UI/Views/LoadingOverlay.axaml`)
Create a reusable `UserControl` or embed it directly in the tab content data template in `MainWindow.axaml`:

```xml
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
```

#### `MainWindow.axaml`
Update `TabControl.ContentTemplate`:
```xml
<TabControl.ContentTemplate>
  <DataTemplate x:DataType="vm:TabViewModel">
    <Grid ColumnDefinitions="*" HorizontalAlignment="Stretch">
      <ListBox ItemsSource="{Binding LogEvents}" ... />
      <views:LoadingOverlay Grid.Column="0" />
    </Grid>
  </DataTemplate>
</TabControl.ContentTemplate>
```

### 4. Theme Compatibility
- Use `DynamicResource SystemAccentColorBrush` or `DynamicResource SystemControlForegroundBaseHighBrush` for the spinner and text so they blend harmoniously in both Light and Dark modes.
- Use a semi-transparent backdrop (`#66000000`) for consistent contrast.

## Testing Plan

### Automated Unit Tests (`LogTail.UI.Tests`)
1. **`StartTailingAsync_WhenInitialLoadStarts_SetsIsLoadingTrue`**:
   - Ensure `tab.IsLoading` is `true` when the tailing process for a new file begins.
2. **`OnInitialLogLoaded_WhenCompleted_SetsIsLoadingFalse`**:
   - Ensure `tab.IsLoading` becomes `false` after the `InitialLogLoaded` event fires and is handled.
3. **`StartTailingAsync_OnError_SetsIsLoadingFalse`**:
   - Ensure `tab.IsLoading` returns to `false` if an exception occurs while streaming the log.

### Manual Verification
1. Run the LogTail application and open a large log file (>50 MB / 100k lines).
2. Verify that the spinner overlay appears with a smooth animation and the text `"Reading log file..."`.
3. Verify that the overlay disappears as soon as the log has finished rendering.
4. Switch the theme (Light / Dark) and verify that the spinner remains visible with good contrast and proportion.
