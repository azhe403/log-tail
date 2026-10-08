namespace LogTail.Core.Models;

public sealed record WindowPlacement(
    int? X = null,
    int? Y = null,
    double? Width = null,
    double? Height = null,
    bool IsMaximized = false);

public sealed record AppSettings(
    ThemeMode Theme = ThemeMode.System,
    int BufferCapacity = 50_000,
    int MaxBufferCapacity = 2_000_000,
    TimeSpan PollInterval = default,
    string DefaultEncoding = "utf-8",
    int TailLineLimit = 50_000,
    int InitialWindowBytes = 8 * 1024 * 1024,
    int MaxWindowBytes = 64 * 1024 * 1024,
    bool RestoreLastSession = true,
    WindowPlacement? WindowPlacement = null);
