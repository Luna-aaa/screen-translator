using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Translate;

/// <summary>
/// The numbered wire format for translating many blocks in one request, and — the part
/// that actually matters — a parser tolerant enough to survive a model that decorates it.
///
/// Every translation has to land back on the exact words it came from, so a misparse is
/// not a cosmetic problem: it draws the wrong sentence over the wrong paragraph, which
/// looks authoritative and is completely wrong. The parser therefore keys on the number
/// the model echoed rather than on line order, and simply leaves a block untranslated
/// when nothing matched it.
/// </summary>
internal static class BatchFormat
{
    /// <summary>Matches "[3] …", "3. …", "【3】…", "3: …" — every shape these models actually emit.</summary>
    private static readonly Regex NumberedLine = new(
        @"^\s*[\[\[【(（]?\s*(\d{1,3})\s*[\]\]】)）]?\s*[.。、:：]?\s*(.*)$",
        RegexOptions.Compiled);

    public static string BuildInput(IReadOnlyList<string> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            // Newlines inside a segment would break the one-line-per-block contract the
            // reply is supposed to mirror, so they are flattened on the way out.
            sb.Append('[').Append(i + 1).Append("] ").Append(Flatten(segments[i]));
        }
        return sb.ToString();
    }

    /// <returns>
    /// One entry per input segment. A null entry means the model did not answer for that
    /// block; the caller leaves the original text showing rather than guessing.
    /// </returns>
    public static string?[] Parse(string reply, int expected)
    {
        var result = new string?[expected];
        if (string.IsNullOrWhiteSpace(reply)) return result;

        var current = -1;
        var buffer = new StringBuilder();

        void Flush()
        {
            if (current >= 0 && current < expected)
            {
                var text = buffer.ToString().Trim();
                if (text.Length > 0) result[current] = text;
            }
            buffer.Clear();
        }

        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            // Models like to wrap a numbered list in a code fence; the fence is not content.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;

            var match = NumberedLine.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number)
                && number >= 1 && number <= expected)
            {
                Flush();
                current = number - 1;
                buffer.Append(match.Groups[2].Value.Trim());
                continue;
            }

            if (line.Trim().Length == 0) continue;

            // A continuation of the block above: the model wrapped a long translation.
            if (current >= 0)
            {
                if (buffer.Length > 0) buffer.Append(' ');
                buffer.Append(line.Trim());
            }
        }

        Flush();
        return result;
    }

    private static string Flatten(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Trim();
}
