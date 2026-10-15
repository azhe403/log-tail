using FluentAssertions;
using LogTail.Core.Diagnostics;
using LogTail.Core.Models;
using Xunit;

namespace LogTail.Core.Tests.Diagnostics;

public sealed class BufferMemoryEstimatorTests
{
    private static EnrichedLogEvent Event(string line) =>
        new(
            new RawLogEvent(DateTimeOffset.UtcNow, "test.log", 0, line),
            LogLevel.Unknown,
            Timestamp: null,
            LevelColorKey: null);

    [Fact]
    public void Estimate_WhenBufferEmpty_ReturnsZero()
    {
        BufferMemoryEstimator.Estimate(Array.Empty<EnrichedLogEvent>()).Should().Be(0);
    }

    [Fact]
    public void Estimate_WhenLineGrows_CountsTwoBytesPerCharacter()
    {
        var shortLine = BufferMemoryEstimator.Estimate(new[] { Event(new string('x', 10)) });
        var longLine = BufferMemoryEstimator.Estimate(new[] { Event(new string('x', 20)) });

        longLine.Should().Be(shortLine + 20);
    }

    [Fact]
    public void Estimate_WhenManyEvents_AddsPerEventOverhead()
    {
        var one = BufferMemoryEstimator.Estimate(new[] { Event("hello") });
        var three = BufferMemoryEstimator.Estimate(
            new[] { Event("hello"), Event("hello"), Event("hello") });

        three.Should().Be(one * 3);
    }

    [Fact]
    public void Estimate_WhenFullBufferOfLongLines_StaysBeyondIntRange()
    {
        // A capacity-sized buffer of capped 4000-char lines is ~400 MB; an int
        // accumulator would have wrapped, which is why the estimator sums in long.
        var events = Enumerable
            .Range(0, 50_000)
            .Select(_ => Event(new string('x', 4000)))
            .ToList();

        var bytes = BufferMemoryEstimator.Estimate(events);

        bytes.Should().BeGreaterThan(400_000_000);
    }

    [Fact]
    public void Estimate_WhenNull_ThrowsArgumentNullException()
    {
        var act = () => BufferMemoryEstimator.Estimate(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
