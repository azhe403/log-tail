using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using LogTail.Core.Logging;
using LogTail.Core.Persistence;
using LogTail.Core.Sources;
using LogTail.UI.ViewModels;
using Xunit;

namespace LogTail.UI.Tests;

[Collection("MainWindowViewModelTests")]
public sealed class MainWindowViewModelSearchStatusTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowViewModelSearchStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-searchstatus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SelectedTabStatus_WhenSearching_ShowsMatchCountInLogArea()
    {
        var logger = new ConsoleLogger();
        var sut = new MainWindowViewModel(new SettingsStore(_tempDir, logger), new LogSourceFactory(logger));
        var path = Path.Combine(_tempDir, "search-status.log");
        File.WriteAllLines(path, new[] { "alpha hit", "beta line", "alpha again" });

        sut.AddTab(path);
        await WaitUntilAsync(() => sut.SelectedTabStatus.Contains("3 lines"), TimeSpan.FromSeconds(10));

        sut.Search.SearchText = "alpha";
        await WaitUntilAsync(() => sut.SelectedTabStatus.Contains("2 results"), TimeSpan.FromSeconds(10));

        // The count stays in the log area even with the results panel closed.
        sut.Search.ClosePanel();
        sut.SelectedTabStatus.Should().Contain("2 results");
    }

    [AvaloniaFact]
    public async Task SelectedTabStatus_WhenQueryCleared_DropsMatchCount()
    {
        var logger = new ConsoleLogger();
        var sut = new MainWindowViewModel(new SettingsStore(_tempDir, logger), new LogSourceFactory(logger));
        var path = Path.Combine(_tempDir, "search-status-clear.log");
        File.WriteAllLines(path, new[] { "alpha hit", "beta line", "alpha again" });

        sut.AddTab(path);
        await WaitUntilAsync(() => sut.SelectedTabStatus.Contains("3 lines"), TimeSpan.FromSeconds(10));

        sut.Search.SearchText = "alpha";
        await WaitUntilAsync(() => sut.SelectedTabStatus.Contains("2 results"), TimeSpan.FromSeconds(10));

        sut.Search.SearchText = string.Empty;
        await WaitUntilAsync(() => !sut.SelectedTabStatus.Contains("results"), TimeSpan.FromSeconds(10));

        sut.SelectedTabStatus.Should().NotContain("results");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }

            if (predicate())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }
}
