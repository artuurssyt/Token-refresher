using System.Text;

namespace LocaltsAccountManager.Infrastructure.Import;

/// <summary>
/// Reads text a line at a time while refusing to materialise more than a fixed number of
/// characters per line. <see cref="TextReader.ReadLine"/> buffers the whole line first, so a
/// dump file with no newlines would allocate the entire file before any length check runs.
/// </summary>
internal static class CappedLineReader
{
    /// <summary>Longest line the import pipeline will accept.</summary>
    public const int MaxLineLength = 65536;

    /// <summary>
    /// Reads the next line, keeping at most <paramref name="maxLength"/> + 1 characters so callers
    /// can still detect an over-long line by testing <c>Length &gt; maxLength</c>. Returns
    /// <see langword="null"/> at end of input. <paramref name="truncated"/> is set when characters
    /// beyond the cap were discarded.
    /// </summary>
    public static string? ReadLine(TextReader reader, int maxLength, out bool truncated)
    {
        truncated = false;

        var next = reader.Read();
        if (next < 0)
        {
            return null;
        }

        // Keep one character past the cap so an exactly-at-the-limit line stays valid while a
        // longer one still trips the caller's Length > maxLength check.
        var keepLimit = maxLength + 1;
        var builder = new StringBuilder();

        while (next >= 0)
        {
            var current = (char)next;

            if (current == '\n')
            {
                break;
            }

            if (current == '\r')
            {
                if (reader.Peek() == '\n')
                {
                    reader.Read();
                }

                break;
            }

            if (builder.Length < keepLimit)
            {
                builder.Append(current);
            }
            else
            {
                truncated = true;
            }

            next = reader.Read();
        }

        return builder.ToString();
    }
}
