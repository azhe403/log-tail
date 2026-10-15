namespace LogTail.Core.Diagnostics;

/// <summary>
/// Samples the current process's own CPU and memory.
/// <para>
/// Abstracted from <see cref="ProcessResourceSampler"/> so the window's status
/// strings can be exercised against fixed readings instead of live counters.
/// </para>
/// </summary>
public interface IProcessResourceSampler
{
    /// <summary>
    /// Takes one reading of this process's CPU and memory. Consecutive calls
    /// share an interval, so the second and later readings are rates; the
    /// first reports zero CPU for exactly that reason. Never throws: an
    /// unreadable counter yields zeroed usage instead.
    /// </summary>
    ResourceUsage Sample();
}
