using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using LogTail.Core.Logging;
using LogTail.Core.Persistence;
using LogTail.Core.Sources;
using LogTail.UI.ViewModels;
using LogTail.UI.Views;
using Xunit;

namespace LogTail.UI.Tests.Views;

public sealed class SearchPanelVisibilityTests : IDisposable
{
    static SearchPanelVisibilityTests()
    {
        // Headless tests: register the Avalonia activation fetcher so
        // ReactiveWindow's WhenActivated can resolve view activation.
        Splat.Locator.CurrentMutable.Register(
            () => new Avalonia.ReactiveUI.AvaloniaActivationForViewFetcher(),
            typeof(ReactiveUI.IActivationForViewFetcher));
    }

    private readonly List<MainWindowViewModel> _created = new();
    private readonly string _tempDir;

    public SearchPanelVisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-searchpanel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var vm in _created)
        {
            vm.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void SearchPanel_WithQuery_BecomesVisible()
    {
        var (window, vm) = CreateWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.Search.SearchText = "error";
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Border>("SearchResultsPanel")!.IsVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SearchPanel_WithoutQuery_StaysHidden()
    {
        var (window, vm) = CreateWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.Search.SearchText = "error";
            Dispatcher.UIThread.RunJobs();

            vm.Search.SearchText = string.Empty;
            Dispatcher.UIThread.RunJobs();

            window.FindControl<Border>("SearchResultsPanel")!.IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    private (MainWindow Window, MainWindowViewModel ViewModel) CreateWindow()
    {
        var logger = new ConsoleLogger();
        var settings = new SettingsStore(_tempDir, logger);

        // Construct the window before the ViewModel: touching RxApp (via the
        // ViewModel's reactive setup) before the first ReactiveWindow ctor
        // breaks the IActivationForViewFetcher resolution and the window ctor
        // throws ArgumentException in headless tests.
        var window = new MainWindow(settings, logger);
        var vm = new MainWindowViewModel(settings, new LogSourceFactory(logger));
        _created.Add(vm);
        window.DataContext = vm;

        return (window, vm);
    }
}
