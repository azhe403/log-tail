using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using LogTail.Core.Logging;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using LogTail.Core.Sources;
using LogTail.UI.ViewModels;
using LogTail.UI.Views;
using ReactiveUI;

namespace LogTail.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Build dependencies.
            var logger = new ConsoleLogger();

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "logtail");
            MigrateLegacyConfig(appDataDir, logger);
            Directory.CreateDirectory(appDataDir);

            var settings = new SettingsStore(appDataDir, logger);
            var sessionStore = new SessionStore(appDataDir, logger);
            var factory = new LogSourceFactory(logger);

            var settingsViewModel = new SettingsViewModel(settings, logger);

            // Apply saved theme and keep it in sync with the Settings dialog.
            ApplyTheme(settingsViewModel.Theme);
            settingsViewModel.ThemeChanged += ApplyTheme;

            var viewModel = new MainWindowViewModel(settings, factory, logger, settingsViewModel);

            // Session tracking. Created before restore so it observes the tabs
            // that restore adds, but disabled until restore finishes to avoid
            // writing mid-restore.
            var tracker = new SessionTracker(
                sessionStore,
                viewModel.Tabs,
                viewModel.WhenAnyValue(x => x.SelectedTab),
                logger);

            // Explicit startup file (headless demo env var; CLI args later) wins
            // over session restore.
            var autoOpen = Environment.GetEnvironmentVariable("LOGTAIL_AUTO_OPEN_FILE");
            var hasExplicitFile = !string.IsNullOrEmpty(autoOpen) && File.Exists(autoOpen);

            if (hasExplicitFile)
            {
                viewModel.CurrentFilePath = autoOpen;
                _ = viewModel.OpenFileAndAddTabAsync(autoOpen!);
            }
            else if (settingsViewModel.RestoreLastSession)
            {
                RestoreSession(viewModel, sessionStore, tracker);
            }

            tracker.IsEnabled = settingsViewModel.RestoreLastSession;
            settingsViewModel.RestoreLastSessionChanged += enabled => tracker.IsEnabled = enabled;
            desktop.Exit += (_, _) => tracker.Dispose();

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void MigrateLegacyConfig(string appDataDir, ILogTailLogger logger)
    {
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "log-tail");
        if (string.Equals(legacyDir, appDataDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var fileName in new[] { "settings.json", "session.json" })
        {
            var target = Path.Combine(appDataDir, fileName);
            var source = Path.Combine(legacyDir, fileName);
            try
            {
                if (!File.Exists(target) && File.Exists(source))
                {
                    Directory.CreateDirectory(appDataDir);
                    File.Copy(source, target);
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to migrate {fileName} from legacy location: {ex.Message}");
            }
        }
    }

    private static void RestoreSession(
        MainWindowViewModel viewModel,
        SessionStore sessionStore,
        SessionTracker tracker)
    {
        var session = sessionStore.Load();

        // Folder tabs first: the sidebar list is built from them, and the
        // active one is re-selected below once the log tabs exist.
        if (session.WorkspaceFolders is { Count: > 0 })
        {
            viewModel.RestoreWorkspaces(session.WorkspaceFolders);
        }

        var valid = new List<string>();
        var missing = new List<string>();
        // Files is declared non-null, but a hand-edited or truncated
        // session.json deserializes it as null, and an NRE here would kill
        // startup with no dialog.
        foreach (var path in session.Files ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (LogFileValidator.TryValidateFile(path, out _))
            {
                valid.Add(path);
            }
            else
            {
                missing.Add(path);
            }
        }

        tracker.SeedUnresolved(missing);

        if (valid.Count > 0)
        {
            viewModel.RestoreTabs(valid, session.ActiveFile);
        }

        // A folder tab was the active tab last session. Nothing else selects
        // it, and RestoreTabs has just claimed SelectedTab for the log side.
        if (session.ActiveFolder is { } activeFolder)
        {
            var folderTab = viewModel.Tabs.FirstOrDefault(t =>
                t.IsFolder &&
                string.Equals(t.FilePath, activeFolder, StringComparison.OrdinalIgnoreCase));

            if (folderTab is not null)
            {
                viewModel.SelectedTab = folderTab;
            }
        }

        if (missing.Count > 0)
        {
            viewModel.StatusMessage = $"{missing.Count} file dari sesi terakhir tidak ditemukan";
        }
    }

    private void ApplyTheme(ThemeMode mode)
    {
        RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
