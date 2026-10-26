using FluentAssertions;
using LogTail.Core.Indexing;
using Xunit;

namespace LogTail.Core.Tests.Indexing;

public sealed class FileLineCounterTests : IDisposable
{
    private readonly string _tempDir;

    public FileLineCounterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-line-counter-test-{Guid.NewGuid():N}");
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
    public async Task CountAsync_WithTrailingNewline_CountsEveryNewline()
    {
        var filePath = Path.Combine(_tempDir, "lines.log");
        await File.WriteAllTextAsync(filePath, "one\ntwo\nthree\n");

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 0);

        result.NewlineCount.Should().Be(3);
        result.EndsWithNewline.Should().BeTrue();
    }

    [Fact]
    public async Task CountAsync_WithoutTrailingNewline_LeavesLastLineUnterminated()
    {
        var filePath = Path.Combine(_tempDir, "no-final-newline.log");
        await File.WriteAllTextAsync(filePath, "one\ntwo\nthree");

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 0);

        result.NewlineCount.Should().Be(2);
        result.EndsWithNewline.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_WithBlankLines_CountsThem()
    {
        var filePath = Path.Combine(_tempDir, "blank-lines.log");
        await File.WriteAllTextAsync(filePath, "one\n\n\nfour\n");

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 0);

        result.NewlineCount.Should().Be(4);
    }

    [Fact]
    public async Task CountAsync_WhenEmpty_ReturnsZero()
    {
        var filePath = Path.Combine(_tempDir, "empty.log");
        await File.WriteAllTextAsync(filePath, string.Empty);

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 0);

        result.NewlineCount.Should().Be(0);
        result.EndsWithNewline.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_AcrossReadBufferBoundaries_StaysExact()
    {
        var filePath = Path.Combine(_tempDir, "large.log");
        var lines = Enumerable.Range(1, 2_000)
            .Select(i => $"line-{i}-{new string('x', 90)}");
        await File.WriteAllLinesAsync(filePath, lines);

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 0);

        result.NewlineCount.Should().Be(2_000);
        result.EndsWithNewline.Should().BeTrue();
    }

    [Fact]
    public async Task CountAsync_WhenByteLimitStopsMidFile_ScansOnlyThatPrefix()
    {
        var filePath = Path.Combine(_tempDir, "prefix.log");
        await File.WriteAllTextAsync(filePath, "one\ntwo\nthree\n");

        var counter = new FileLineCounter();
        var result = await counter.CountAsync(filePath, byteLimit: 4);

        result.NewlineCount.Should().Be(1);
        result.EndsWithNewline.Should().BeTrue();
    }
}
