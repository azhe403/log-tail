using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using LogTail.UI.Views;
using Xunit;

namespace LogTail.UI.Tests.Views;

public sealed class MainWindowPlacementTests : IDisposable
{
    static MainWindowPlacementTests()
    {
        Splat.Locator.CurrentMutable.Register(
            () => new Avalonia.ReactiveUI.AvaloniaActivationForViewFetcher(),
            typeof(ReactiveUI.IActivationForViewFetcher));
    }

    private readonly string _tempDir;

    public MainWindowPlacementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-window-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup error: {ex.Message}");
            }
        }
    }

    [AvaloniaFact]
    public void RestoreWindowPlacement_RestoresWidthHeightAndMaximized()
    {
        var store = new SettingsStore(_tempDir);
        store.Save(new AppSettings(
            WindowPlacement: new WindowPlacement(
                X: 100,
                Y: 150,
                Width: 1200,
                Height: 800,
                IsMaximized: true)));

        var window = new MainWindow(store, new ConsoleLogger());

        window.Width.Should().Be(1200);
        window.Height.Should().Be(800);
        window.WindowState.Should().Be(WindowState.Maximized);
    }

    [AvaloniaFact]
    public void SaveWindowPlacement_OnClose_SavesPlacement()
    {
        var store = new SettingsStore(_tempDir);
        var window = new MainWindow(store, new ConsoleLogger())
        {
            Width = 1100,
            Height = 700
        };

        window.Close();

        var loaded = store.Load();
        loaded.WindowPlacement.Should().NotBeNull();
        loaded.WindowPlacement!.Width.Should().Be(1100);
        loaded.WindowPlacement!.Height.Should().Be(700);
        loaded.WindowPlacement!.IsMaximized.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task PositionChanged_DebouncesAndSavesPlacement()
    {
        var store = new SettingsStore(_tempDir);
        var window = new MainWindow(store, new ConsoleLogger())
        {
            Width = 1000,
            Height = 650
        };

        window.NotifyPositionChanged(new Avalonia.PixelPoint(320, 240));

        await Task.Delay(400);

        var loaded = store.Load();
        loaded.WindowPlacement.Should().NotBeNull();
        loaded.WindowPlacement!.X.Should().Be(320);
        loaded.WindowPlacement!.Y.Should().Be(240);

        window.Close();
    }
}
