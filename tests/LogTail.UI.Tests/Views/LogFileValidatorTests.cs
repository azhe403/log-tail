using FluentAssertions;
using LogTail.UI.Views;
using Xunit;

namespace LogTail.UI.Tests.Views;

public class LogFileValidatorTests : IDisposable
{
    private readonly string _tempDir;

    public LogFileValidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-validator-test-{Guid.NewGuid():N}");
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
    public void TryValidateFile_WhenFileNotFound_ReturnsFalseAndReportsError()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist.log");

        var result = LogFileValidator.TryValidateFile(nonExistent, out var error);

        result.Should().BeFalse();
        error.Should().StartWith("Error: File not found");
        error.Should().Contain(nonExistent);
    }

    [Fact]
    public void TryValidateFile_WhenFolder_ReturnsFalseAndSilent()
    {
        var folderPath = Path.Combine(_tempDir, "subfolder");
        Directory.CreateDirectory(folderPath);

        var result = LogFileValidator.TryValidateFile(folderPath, out var error);

        result.Should().BeFalse();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateFile_WhenUnsupportedExtension_ReturnsFalseAndSilent()
    {
        var path = Path.Combine(_tempDir, "data.csv");
        File.WriteAllText(path, "a,b,c\n1,2,3\n");

        var result = LogFileValidator.TryValidateFile(path, out var error);

        result.Should().BeFalse();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateFile_WhenValidLogFile_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(path, "INFO: started\n");

        var result = LogFileValidator.TryValidateFile(path, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateFile_WhenValidTxtFile_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(path, "some log line\n");

        var result = LogFileValidator.TryValidateFile(path, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidateFile_WhenValidJsonlFile_ReturnsTrue()
    {
        // The workspace sidebar lists .jsonl, so opening one has to be allowed,
        // or the tab silently disappears on the next restore.
        var path = Path.Combine(_tempDir, "access.jsonl");
        File.WriteAllText(path, "{\"a\":1}\n");

        var result = LogFileValidator.TryValidateFile(path, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
        LogFileValidator.GetSupportedExtension(path).Should().Be(".jsonl");
    }

    [Theory]
    [InlineData("test.log")]
    [InlineData("test.LOG")]
    [InlineData("test.TXT")]
    [InlineData("test.Txt")]
    public void GetSupportedExtension_IsCaseInsensitive(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "x\n");

        var result = LogFileValidator.TryValidateFile(path, out _);

        result.Should().BeTrue();
    }

    [Fact]
    public void TryValidateFile_WhenFileOpenWithSharedReadWrite_ReturnsTrue()
    {
        // Log files are normally being actively written to by another process.
        // Validator must not false-positive on a file opened with the same
        // FileShare flags FileTailSource uses (ReadWrite | Delete).
        var path = Path.Combine(_tempDir, "live.log");
        File.WriteAllText(path, "INFO: started\n");

        using var concurrentStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var result = LogFileValidator.TryValidateFile(path, out var error);

        result.Should().BeTrue();
        error.Should().BeNull();
    }
}
