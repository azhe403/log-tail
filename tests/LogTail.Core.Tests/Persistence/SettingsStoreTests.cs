using FluentAssertions;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using Xunit;

namespace LogTail.Core.Tests.Persistence;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaultSettings()
    {
        var sut = new SettingsStore(_tempDir);

        var result = sut.Load();

        result.Should().Be(new AppSettings());
    }

    [Fact]
    public void Save_WhenSettingsSaved_RoundtripsSuccessfully()
    {
        var sut = new SettingsStore(_tempDir);
        var settings = new AppSettings(Theme: ThemeMode.Dark);

        sut.Save(settings);
        var loaded = sut.Load();

        loaded.Should().Be(settings);
    }

    [Fact]
    public void Update_WhenInvoked_ModifiesAndPersistsSettings()
    {
        var sut = new SettingsStore(_tempDir);
        sut.Save(new AppSettings());

        sut.Update(s => s with { Theme = ThemeMode.Light });
        var loaded = sut.Load();

        loaded.Theme.Should().Be(ThemeMode.Light);
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_ReturnsDefaultSettings()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, "{{{{not json}}}}");

        var sut = new SettingsStore(_tempDir);

        var result = sut.Load();

        result.Should().Be(new AppSettings());
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_DefaultWindowLimitsMatchSpec()
    {
        var sut = new SettingsStore(_tempDir);

        var settings = sut.Load();

        settings.TailLineLimit.Should().Be(50_000);
        settings.InitialWindowBytes.Should().Be(8 * 1024 * 1024);
        settings.MaxWindowBytes.Should().Be(64 * 1024 * 1024);
    }

    [Fact]
    public void Save_WhenCustomLimitsConfigured_RoundtripsSuccessfully()
    {
        var sut = new SettingsStore(_tempDir);
        var custom = new AppSettings(
            TailLineLimit: 10_000,
            InitialWindowBytes: 4 * 1024 * 1024,
            MaxWindowBytes: 32 * 1024 * 1024);

        sut.Save(custom);

        var loaded = sut.Load();

        loaded.TailLineLimit.Should().Be(10_000);
        loaded.InitialWindowBytes.Should().Be(4 * 1024 * 1024);
        loaded.MaxWindowBytes.Should().Be(32 * 1024 * 1024);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_RestoreLastSessionDefaultsTrue()
    {
        var sut = new SettingsStore(_tempDir);

        sut.Load().RestoreLastSession.Should().BeTrue();
    }

    [Fact]
    public void Save_WhenRestoreDisabled_RoundtripsFalse()
    {
        var sut = new SettingsStore(_tempDir);

        sut.Save(new AppSettings(RestoreLastSession: false));

        sut.Load().RestoreLastSession.Should().BeFalse();
    }

    [Fact]
    public void Save_WhenWindowPlacementConfigured_RoundtripsSuccessfully()
    {
        var sut = new SettingsStore(_tempDir);
        var placement = new WindowPlacement(
            X: 120,
            Y: 240,
            Width: 1024,
            Height: 768,
            IsMaximized: true);

        sut.Save(new AppSettings(WindowPlacement: placement));

        var loaded = sut.Load();

        loaded.WindowPlacement.Should().Be(placement);
    }
}
