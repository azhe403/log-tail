using LogTail.Core.Models;

namespace LogTail.UI.ViewModels;

/// <summary>
/// One search hit: the log event that matched plus the tab it belongs to,
/// so a click can activate that tab and the view can scroll to the line.
/// </summary>
public sealed record LogSearchMatch(TabViewModel Tab, EnrichedLogEvent Event)
{
    public string FileName => Tab.FileName;

    public int LineNumber => Event.LineNumber;

    public string Line => Event.Raw.Line;
}
