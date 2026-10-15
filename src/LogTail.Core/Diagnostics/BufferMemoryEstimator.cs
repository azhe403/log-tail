using LogTail.Core.Models;

namespace LogTail.Core.Diagnostics;

/// <summary>
/// Estimates how much memory a tab's rendered log buffer holds.
/// <para>
/// Every tab keeps its own <see cref="EnrichedLogEvent"/> objects in its
/// <c>LogEvents</c> collection, and those objects — not the references the shared ring
/// buffer holds — are the bulk of the app's RAM. So a per-tab estimate is also a
/// breakdown of where that RAM went, which the single app-wide number cannot answer.
/// </para>
/// <para>
/// Exact attribution needs a profiler; this is a deliberately simple estimate:
/// 2 bytes per character of the rendered line (UTF-16) plus a fixed per-event cost
/// covering both record headers, the string objects, the timestamp and the line number.
/// The UI marks the result with "~".
/// </para>
/// <para>
/// Summed in <see cref="long"/> on purpose: a full buffer of capped 4000-character lines
/// is hundreds of megabytes and would overflow an int accumulator.
/// </para>
/// </summary>
public static class BufferMemoryEstimator
{
    /// <summary>Bytes per UTF-16 character in a rendered line.</summary>
    private const int BytesPerChar = 2;

    /// <summary>
    /// Fixed cost of one buffered event: the <see cref="EnrichedLogEvent"/> and
    /// <see cref="RawLogEvent"/> records, their string headers, the timestamp and the
    /// line number. Amortizes the per-source file name every event carries.
    /// </summary>
    private const int EventOverheadBytes = 140;

    /// <summary>
    /// Estimated bytes currently held by <paramref name="events"/>. Zero when empty.
    /// </summary>
    public static long Estimate(IEnumerable<EnrichedLogEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        long total = 0;
        foreach (var item in events)
        {
            total += EventOverheadBytes + ((long)BytesPerChar * item.Raw.Line.Length);
        }

        return total;
    }
}
