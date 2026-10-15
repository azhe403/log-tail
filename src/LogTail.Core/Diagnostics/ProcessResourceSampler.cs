using System.ComponentModel;
using System.Diagnostics;

namespace LogTail.Core.Diagnostics;

/// <summary>
/// One sample of this process's own resource usage.
/// </summary>
/// <param name="CpuPercent">
/// Share of the machine's total CPU capacity the process used since the previous sample,
/// in percent (100 = one core fully busy on a single-core box, 50 = one core fully busy
/// on two cores). Zero on the first sample: a rate needs an interval to divide by.
/// </param>
/// <param name="WorkingSetBytes">Resident memory of the process right now.</param>
public readonly record struct ResourceUsage(double CpuPercent, long WorkingSetBytes);

/// <summary>
/// Samples the current process's CPU and memory for the status bar.
/// <para>
/// Cross-platform through <see cref="Process"/>: <c>TotalProcessorTime</c> and
/// <c>WorkingSet64</c> both work on Windows, Linux and macOS.
/// </para>
/// <para>
/// CPU is a rate, so <see cref="Sample"/> keeps the previous reading and divides the
/// processor-time delta by the wall-clock delta scaled by the core count — which is why
/// the first sample returns 0%: there is no interval yet.
/// </para>
/// <para>
/// Never throws on a failed read. A status bar must not be able to break the app, so an
/// unreadable counter yields zeroed usage and the UI keeps the last good string. Not
/// thread-safe: call it from the UI thread, like the rest of the view models.
/// </para>
/// </summary>
public sealed class ProcessResourceSampler : IProcessResourceSampler, IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _previousProcessorTime;
    private DateTime _previousSampleUtc;
    private bool _disposed;

    /// <summary>
    /// Reads the counters now, advancing the interval the next call measures against.
    /// </summary>
    public ResourceUsage Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // Refresh BEFORE reading: a Process object hands out whichever counters
            // were captured at its last refresh. Without this the working set would
            // freeze at the very first reading and the bar would under-report the
            // app's real memory for the rest of the session — the exact bug this
            // refreshes away.
            _process.Refresh();

            var nowUtc = DateTime.UtcNow;
            var processorTime = _process.TotalProcessorTime;
            var workingSet = _process.WorkingSet64;

            var cpu = ComputeCpuPercent(processorTime, nowUtc);

            _previousProcessorTime = processorTime;
            _previousSampleUtc = nowUtc;

            return new ResourceUsage(cpu, workingSet);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Some containers/CI expose no readable counters. Reporting zero keeps the
            // status bar honest ("nothing measured") rather than throwing out of a timer.
            // ResourceUsage is a record struct: default would zero it too, but naming
            // the values states the intent out loud.
            return new ResourceUsage(0, 0);
        }
    }

    /// <summary>
    /// CPU used since the previous sample as a share of the machine's total capacity.
    /// Clamped because clock jitter between the two reads can push the ratio a hair
    /// above 100% for a fully busy process.
    /// </summary>
    private double ComputeCpuPercent(TimeSpan processorTime, DateTime nowUtc)
    {
        if (_previousSampleUtc == default)
        {
            return 0;
        }

        var elapsed = nowUtc - _previousSampleUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var cpuDelta = processorTime - _previousProcessorTime;
        if (cpuDelta <= TimeSpan.Zero)
        {
            return 0;
        }

        var cores = Math.Max(1, Environment.ProcessorCount);
        var percent = cpuDelta.TotalSeconds / (elapsed.TotalSeconds * cores) * 100.0;

        return Math.Clamp(percent, 0, 100);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}
