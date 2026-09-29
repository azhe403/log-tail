using System.Collections.Generic;

namespace LogTail.Core.Models;

/// <summary>
/// Persisted session. <paramref name="Files"/> holds log-file tabs only;
/// <paramref name="WorkspaceFolders"/> holds one entry per open folder tab, so
/// restore can rebuild both kinds of tab. Session files written before folder
/// tabs existed deserialize with null/empty workspace values.
/// </summary>
public sealed record SessionState(
    IReadOnlyList<string> Files,
    string? ActiveFile,
    IReadOnlyList<string>? WorkspaceFolders = null,
    string? ActiveFolder = null);
