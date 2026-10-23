using System.Collections.Generic;
using LogTail.Core.Workspace;

namespace LogTail.Core.Models;

/// <summary>
/// One workspace folder's saved sidebar sort, so a folder comes back sorted the
/// way the user left it instead of resetting to Name ascending every restart.
/// </summary>
public sealed record WorkspaceSortState(
    string FolderPath,
    WorkspaceSortCriterion Criterion,
    WorkspaceSortDirection Direction);

/// <summary>
/// Persisted session. <paramref name="Files"/> holds log-file tabs only;
/// <paramref name="WorkspaceFolders"/> holds one entry per open folder tab, so
/// restore can rebuild both kinds of tab. <paramref name="WorkspaceSorts"/> pairs
/// each folder with its sort, matched to a tab by path on restore. Session files
/// written before these fields existed deserialize with null/empty values.
/// </summary>
public sealed record SessionState(
    IReadOnlyList<string> Files,
    string? ActiveFile,
    IReadOnlyList<string>? WorkspaceFolders = null,
    string? ActiveFolder = null,
    IReadOnlyList<WorkspaceSortState>? WorkspaceSorts = null,
    string? ActivePreview = null,
    IReadOnlyList<string?>? WorkspacePreviewFiles = null,
    string? LastOpenedFile = null);