namespace LogTail.Core.Sources;

public interface ILogSource : IAsyncDisposable
{
    string DisplayName { get; }

    IObservable<Models.RawLogEvent> Events { get; }

    bool IsRunning { get; }

    /// <summary>
    /// Raised once after all historical lines have been emitted to <see cref="Events"/>.
    /// Not raised again on file rotation; only on first successful attach.
    /// </summary>
    event Action? InitialLogLoaded;

    /// <summary>
    /// Size of the tailed file in bytes at the time of attachment.
    /// </summary>
    long FileSize { get; }

    /// <summary>
    /// Total number of lines in the tailed file, counted from byte zero.
    /// Null while the initial count is still being computed, so callers can
    /// fall back to the number of lines they have rendered.
    /// </summary>
    long? TotalLineCount { get; }

    Task StartAsync(CancellationToken ct);

    ValueTask StopAsync();

    void SeekToOffset(long offset);
}
