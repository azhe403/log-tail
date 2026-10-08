using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LogTail.Core.Workspace;
using Xunit;

namespace LogTail.Core.Tests.Workspace;

public sealed class WorkspaceFileScannerTests : IDisposable
{
    private readonly string _root;

    public WorkspaceFileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"logtail-ws-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Scan_ListsLogFilesRecursivelySortedByName()
    {
        WriteFile("zeta.log");
        WriteFile("Alpha.log");
        WriteFile("2026-09-25", "nested.log");

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Select(f => f.Name).Should().Equal("Alpha.log", "nested.log", "zeta.log");
        files.Select(f => f.Name).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ExposesFullPathForEveryFile()
    {
        WriteFile("sub", "app.log");

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Single().Path.Should().Be(Path.Combine(_root, "sub", "app.log"));
    }

    [Fact]
    public void Scan_SortsByFullPathWhenTwoFilesShareAName()
    {
        WriteFile("b-dir", "same.log");
        WriteFile("a-dir", "same.log");

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Should().HaveCount(2);
        files.Select(f => f.Path).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_FiltersExtensionsCaseInsensitively()
    {
        WriteFile("a.LOG");
        WriteFile("b.Jsonl");
        WriteFile("c.txt");
        WriteFile("d.md");
        WriteFile("noext");

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Select(f => f.Name).Should().Equal("a.LOG", "b.Jsonl", "c.txt");
    }

    [Fact]
    public void Scan_WhenFileCountExceedsCap_TruncatesAndReportsOverflow()
    {
        for (var i = 0; i < WorkspaceFileScanner.MaxFiles + 3; i++)
        {
            WriteFile($"file{i:D5}.log");
        }

        var files = WorkspaceFileScanner.Scan(_root, out var overflow, out _);

        files.Should().HaveCount(WorkspaceFileScanner.MaxFiles);
        overflow.Should().Be(3);
    }

    [Fact]
    public void Scan_WhenRootDoesNotExist_ReturnsEmptyWithError()
    {
        var files = WorkspaceFileScanner.Scan(Path.Combine(_root, "nope"), out var overflow, out var error);

        files.Should().BeEmpty();
        overflow.Should().Be(0);
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Scan_DoesNotDescendIntoDirectoryLinks()
    {
        // A link back into an ancestor is the one way a subtree can re-enter
        // itself, and a visited-path set cannot see it: each hop is a
        // distinct string.
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllText(Path.Combine(_root, "real", "app.log"), "x");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating links needs extra privileges on some Windows setups and
            // is unavailable on some platforms. Nothing to assert here.
            return;
        }

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Select(f => f.Name).Should().Equal("app.log");
    }

    [Fact]
    public void Scan_WhenFolderNestsDeeperThanTheDepthLimit_StopsWalking()
    {
        var deep = _root;
        for (var i = 0; i < WorkspaceFileScanner.MaxDepth + 20; i++)
        {
            deep = Path.Combine(deep, "d");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "deep.log"), "x");

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        files.Select(f => f.Name).Should().NotContain("deep.log");
    }

    [Fact]
    public void Scan_ReportsSizeAndModifiedTimeForTheSidebar()
    {
        var path = WriteFile("sized.log");
        File.WriteAllText(path, new string('x', 2048));

        var files = WorkspaceFileScanner.Scan(_root, out _, out _);

        var file = files.Single();
        file.Size.Should().Be(2048);
        file.SizeText.Should().Be("2 KB");
        file.LastModified.Should().BeCloseTo(File.GetLastWriteTime(path), TimeSpan.FromSeconds(5));
        file.ModifiedText.Should().MatchRegex(@"^\d{2}-\d{2} \d{2}:\d{2}$");
    }

    [Fact]
    public void SizeText_UsesUnitsUpToGigabytes()
    {
        WorkspaceFile.FormatSize(512).Should().Be("512 B");
        WorkspaceFile.FormatSize(2048).Should().Be("2 KB");
        WorkspaceFile.FormatSize(5L * 1024 * 1024).Should().Be("5 MB");
        WorkspaceFile.FormatSize(3L * 1024 * 1024 * 1024).Should().Be("3 GB");
    }

    [Fact]
    public void OrderFiles_SortsByNameAscendingAndDescending()
    {
        var files = new[]
        {
            new WorkspaceFile("c.log", "c.log", 100, DateTime.Now),
            new WorkspaceFile("a.log", "a.log", 200, DateTime.Now),
            new WorkspaceFile("b.log", "b.log", 300, DateTime.Now),
        };

        var asc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Name, WorkspaceSortDirection.Ascending);
        asc.Select(f => f.Name).Should().Equal("a.log", "b.log", "c.log");

        var desc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Name, WorkspaceSortDirection.Descending);
        desc.Select(f => f.Name).Should().Equal("c.log", "b.log", "a.log");
    }

    [Fact]
    public void OrderFiles_SortsBySizeAscendingAndDescending()
    {
        var files = new[]
        {
            new WorkspaceFile("small.log", "small.log", 10, DateTime.Now),
            new WorkspaceFile("large.log", "large.log", 1000, DateTime.Now),
            new WorkspaceFile("medium.log", "medium.log", 100, DateTime.Now),
        };

        var asc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Size, WorkspaceSortDirection.Ascending);
        asc.Select(f => f.Name).Should().Equal("small.log", "medium.log", "large.log");

        var desc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Size, WorkspaceSortDirection.Descending);
        desc.Select(f => f.Name).Should().Equal("large.log", "medium.log", "small.log");
    }

    [Fact]
    public void OrderFiles_SortsByDateAscendingAndDescending()
    {
        var now = DateTime.UtcNow;
        var files = new[]
        {
            new WorkspaceFile("old.log", "old.log", 100, now.AddHours(-2)),
            new WorkspaceFile("newest.log", "newest.log", 100, now),
            new WorkspaceFile("mid.log", "mid.log", 100, now.AddHours(-1)),
        };

        var asc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Date, WorkspaceSortDirection.Ascending);
        asc.Select(f => f.Name).Should().Equal("old.log", "mid.log", "newest.log");

        var desc = WorkspaceFileScanner.OrderFiles(files, WorkspaceSortCriterion.Date, WorkspaceSortDirection.Descending);
        desc.Select(f => f.Name).Should().Equal("newest.log", "mid.log", "old.log");
    }

    private string WriteFile(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "line");
        return path;
    }
}
