using System;
using System.IO;
using FluentAssertions;
using LogTail.Core.Models;
using LogTail.Core.Persistence;
using Xunit;

namespace LogTail.Core.Tests.Persistence;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"logtail-session-test-{Guid.NewGuid():N}");
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
    public void Load_WhenFileDoesNotExist_ReturnsEmpty()
    {
        var sut = new SessionStore(_tempDir);

        var result = sut.Load();

        result.Files.Should().BeEmpty();
        result.ActiveFile.Should().BeNull();
    }

    [Fact]
    public void Save_WhenSaved_RoundtripsFilesAndActive()
    {
        var sut = new SessionStore(_tempDir);
        var state = new SessionState(new[] { "a.log", "b.log" }, "b.log");

        sut.Save(state);
        var loaded = sut.Load();

        loaded.Files.Should().Equal("a.log", "b.log");
        loaded.ActiveFile.Should().Be("b.log");
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "session.json"), "{{{{not json}}}}");
        var sut = new SessionStore(_tempDir);

        var result = sut.Load();

        result.Files.Should().BeEmpty();
        result.ActiveFile.Should().BeNull();
    }

    [Fact]
    public void Load_WhenJsonOmitsWorkspaceFields_LeavesWorkspaceEmpty()
    {
        // Sessions written before folder tabs existed must keep loading.
        File.WriteAllText(
            Path.Combine(_tempDir, "session.json"),
            """
            { "Files": [ "a.log" ], "ActiveFile": "a.log" }
            """);
        var sut = new SessionStore(_tempDir);

        var result = sut.Load();

        result.Files.Should().Equal("a.log");
        result.ActiveFile.Should().Be("a.log");
        result.WorkspaceFolders.Should().BeNull();
        result.ActiveFolder.Should().BeNull();
    }

    [Fact]
    public void Save_WhenWorkspaceFoldersPresent_RoundtripsFoldersAndActiveFolder()
    {
        var sut = new SessionStore(_tempDir);
        var state = new SessionState(
            new[] { "a.log" },
            "a.log",
            new[] { @"C:\logs\app", @"C:\logs\web" },
            @"C:\logs\app");

        sut.Save(state);
        var loaded = sut.Load();

        loaded.Files.Should().Equal("a.log");
        loaded.WorkspaceFolders.Should().Equal(@"C:\logs\app", @"C:\logs\web");
        loaded.ActiveFolder.Should().Be(@"C:\logs\app");
    }
}
