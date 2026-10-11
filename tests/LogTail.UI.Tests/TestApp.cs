using Avalonia;
using Avalonia.Headless;
using Avalonia.ReactiveUI;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AvaloniaTestApplication(typeof(LogTail.UI.Tests.TestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace LogTail.UI.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .UseReactiveUI();
    }
}
