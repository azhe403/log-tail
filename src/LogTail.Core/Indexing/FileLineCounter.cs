using System.Buffers;

namespace LogTail.Core.Indexing;

/// <summary>
/// Result of scanning a byte range for line terminators.
/// </summary>
/// <param name="NewlineCount">Number of <c>\n</c> bytes found.</param>
/// <param name="EndsWithNewline">True when the scanned range's last byte is <c>\n</c>.</param>
public readonly record struct LineScanResult(long NewlineCount, bool EndsWithNewline);

/// <summary>
/// Counts the lines in a file by scanning its bytes once. No string is
/// allocated per line, so this stays cheap enough to run on a background
/// thread for multi-gigabyte logs.
/// <para>
/// A line is a newline-terminated run of bytes; a final run without a
/// trailing newline still counts as one line, matching how editors number
/// the last line of a file.
/// </para>
/// </summary>
public sealed class FileLineCounter
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Scans <paramref name="filePath"/> from byte zero to
    /// <paramref name="byteLimit"/> (or EOF when it is zero) and returns the
    /// newline count of that range.
    /// </summary>
    public async Task<LineScanResult> CountAsync(
        string filePath,
        long byteLimit,
        CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var limit = byteLimit <= 0 ? stream.Length : Math.Min(byteLimit, stream.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long newlines = 0;
        long remaining = limit;
        var sawBytes = false;
        var endsWithNewline = false;

        try
        {
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(remaining, BufferSize);
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, toRead), ct)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                var chunk = new ReadOnlySpan<byte>(buffer, 0, read);
                newlines += CountNewlines(chunk);
                endsWithNewline = chunk[^1] == (byte)'\n';
                sawBytes = true;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new LineScanResult(newlines, sawBytes && endsWithNewline);
    }

    /// <summary>
    /// Counts <c>\n</c> bytes in <paramref name="bytes"/> without allocating.
    /// </summary>
    public static long CountNewlines(ReadOnlySpan<byte> bytes)
    {
        long count = 0;
        while (!bytes.IsEmpty)
        {
            var index = bytes.IndexOf((byte)'\n');
            if (index < 0)
            {
                break;
            }

            count++;
            bytes = bytes[(index + 1)..];
        }

        return count;
    }
}
