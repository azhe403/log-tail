using System.Diagnostics;
using FluentAssertions;
using LogTail.Core.Diagnostics;
using Xunit;

namespace LogTail.Core.Tests.Diagnostics;

/// <summary>
/// Shares a collection with <see cref="BufferMemoryEstimatorTests"/>: that class
/// allocates ~400 MB in one test, and resident memory is process-wide state, so
/// letting the two classes overlap makes any working-set reading meaningless.
/// </summary>
[Collection("process memory")]
public sealed class ProcessResourceSamplerTests
{
    [Fact]
    public void Sample_WhenCalled_ReportsPositiveWorkingSet()
    {
        using var sut = new ProcessResourceSampler();

        var usage = sut.Sample();

        usage.WorkingSetBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sample_WhenCalledFirstTime_ReportsZeroCpu()
    {
        // A rate has no interval to divide by until the second sample.
        using var sut = new ProcessResourceSampler();

        sut.Sample().CpuPercent.Should().Be(0);
    }

    [Fact]
    public void Sample_WhenProcessBurnsCpu_ReportsNonZeroCpu()
    {
        // Only "greater than 0" is asserted, and that cannot flake: the thread
        // spins until 100ms of wall clock has passed, so it accrues processor
        // time for every share of the CPU it gets, however small.
        using var sut = new ProcessResourceSampler();
        sut.Sample();

        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 100)
        {
        }

        var usage = sut.Sample();

        usage.CpuPercent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Sample_WhenCalledTwice_KeepsCpuWithinTotalCapacity()
    {
        using var sut = new ProcessResourceSampler();
        sut.Sample();
        Thread.Sleep(50);

        var usage = sut.Sample();

        // No lower bound asserted here: ComputeCpuPercent clamps to [0, 100], so
        // ">= 0" would hold by construction and test nothing. The upper bound is
        // the contract worth guarding — a share of total capacity cannot exceed
        // everything.
        usage.CpuPercent.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Sample_WhenDisposed_ThrowsObjectDisposedException()
    {
        var sut = new ProcessResourceSampler();
        sut.Dispose();

        var act = () => sut.Sample();

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        var sut = new ProcessResourceSampler();

        sut.Dispose();

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Sample_WhenMemoryGrows_TracksTheNewWorkingSet()
    {
        // Regression guard: the status bar showed ~113 MB while Task Manager
        // showed ~600 MB. Hypothesis was a cached Process object serving stale
        // counters — this proves each reading reflects the process right now.
        //
        // The comparison is against an independent reading taken at the same
        // instant, not against an earlier sample. Growth since an earlier sample
        // is not a property of the sampler: the resident set of a test host also
        // moves when sibling tests allocate and free memory, and macOS returns
        // freed pages to the OS while Windows keeps them resident. Comparing two
        // historical samples therefore held on Windows and failed on the macOS
        // CI runners, once ~300 MB of another test's heap disappeared between
        // them.
        using var sut = new ProcessResourceSampler();

        // Prime the interval. A Process object that never refreshes keeps
        // serving the counters it read here.
        sut.Sample();

        var hog = new List<byte[]>();
        for (var i = 0; i < 6; i++)
        {
            hog.Add(new byte[10 * 1024 * 1024]);
        }

        // Touch every page: a zero-filled array stays mapped to the shared zero
        // page and never grows the working set on its own.
        foreach (var chunk in hog)
        {
            Array.Fill(chunk, (byte)0xAB);
        }

        var after = sut.Sample().WorkingSetBytes;

        using var live = Process.GetCurrentProcess();
        live.Refresh();

        // A frozen counter still reports the primed reading, which these 60 MB
        // of touched pages put far outside the tolerance; two live reads taken
        // microseconds apart agree. Direction does not matter, so a heap that
        // shrinks elsewhere cannot fail the test.
        var drift = Math.Abs(after - live.WorkingSet64);
        drift.Should().BeLessThan(
            16L * 1024 * 1024,
            "the sampler reads the same live counter an independent Process object reads");

        // Keep the pages reachable until the reading above is taken.
        GC.KeepAlive(hog);
    }
}
