using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NcmForge.Core.Lyrics;

/// <summary>One timed lyric line.</summary>
public sealed record LyricLine(TimeSpan Timestamp, string Text);

/// <summary>
/// Parsed lyrics, in the format NetEase ships alongside a download.
/// </summary>
/// <remarks>
/// NetEase's sidecar files are a hybrid: leading JSON lines carry credits with millisecond
/// timestamps, followed by ordinary LRC lines. Rendering normalises both into plain LRC so the
/// result is what music players actually expect to find in a lyrics tag.
/// </remarks>
public sealed class LyricDocument
{
    private static readonly Regex TimedLine = new(
        @"^(?<stamps>(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+)(?<text>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches one bracket timestamp. Used against the stamps prefix rather than relying on group
    /// captures: a repeated capturing group reports only its final repetition, so a line carrying
    /// several timestamps would otherwise be read as a single malformed one.
    /// </summary>
    private static readonly Regex AnyTimestamp = new(
        @"\[(?<stamp>\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?)\]",
        RegexOptions.Compiled);

    private static readonly Regex TagLine = new(
        @"^\[(?<key>ti|ar|al|by|offset|re|ve|length):(?<value>.*)\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WordTiming = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    private LyricDocument(List<LyricLine> lines) => Lines = lines;

    public IReadOnlyList<LyricLine> Lines { get; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Parses hybrid NetEase or plain LRC content.</summary>
    public static LyricDocument Parse(string content)
    {
        var lines = new List<LyricLine>();

        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '{')
            {
                if (TryParseCreditLine(line, out LyricLine credit))
                {
                    lines.Add(credit);
                }

                continue;
            }

            Match tag = TagLine.Match(line);
            if (tag.Success)
            {
                // [ti:] [ar:] [offset:] and friends carry no lyric text.
                continue;
            }

            Match timed = TimedLine.Match(line);
            if (timed.Success)
            {
                string text = WordTiming.Replace(timed.Groups["text"].Value, string.Empty).TrimEnd();

                foreach (Match stamp in AnyTimestamp.Matches(timed.Groups["stamps"].Value))
                {
                    if (TryParseTimestamp(stamp.Value, out TimeSpan parsed))
                    {
                        lines.Add(new LyricLine(parsed, text));
                    }
                }

                continue;
            }

            // Untimed trailing text: keep it, anchored to the last known position.
            lines.Add(new LyricLine(lines.Count > 0 ? lines[^1].Timestamp : TimeSpan.Zero, line.Trim()));
        }

        // Stable sort so credits sharing a timestamp keep their authored order.
        return new LyricDocument(lines.OrderBy(l => l.Timestamp).ToList());
    }

    /// <summary>Renders standard LRC, the form players read for synchronised display.</summary>
    public string ToLrc()
    {
        var builder = new StringBuilder();
        foreach (LyricLine line in Lines)
        {
            TimeSpan t = line.Timestamp;
            builder.Append('[')
                .Append((int)t.TotalMinutes).Append(':')
                .Append(t.Seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append('.')
                .Append(t.Milliseconds.ToString("000", System.Globalization.CultureInfo.InvariantCulture))
                .Append(']')
                .Append(line.Text)
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Renders the text alone, for players that only display unsynchronised lyrics.</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        foreach (LyricLine line in Lines)
        {
            builder.Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads one of NetEase's <c>{"t":0,"c":[{"tx":"..."}]}</c> credit lines.
    /// </summary>
    private static bool TryParseCreditLine(string line, out LyricLine result)
    {
        result = new LyricLine(TimeSpan.Zero, string.Empty);

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            double milliseconds = root.TryGetProperty("t", out JsonElement t) && t.TryGetDouble(out double value)
                ? value
                : 0;

            if (!root.TryGetProperty("c", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var text = new StringBuilder();
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("tx", out JsonElement tx) && tx.GetString() is { } piece)
                {
                    text.Append(piece);
                }
            }

            string combined = text.ToString().Trim();
            if (combined.Length == 0)
            {
                return false;
            }

            result = new LyricLine(TimeSpan.FromMilliseconds(milliseconds), combined);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseTimestamp(string token, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        if (token.Length < 2 || token[0] != '[' || token[^1] != ']')
        {
            return false;
        }

        string inner = token[1..^1];
        int colon = inner.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        if (!int.TryParse(inner[..colon], out int minutes))
        {
            return false;
        }

        string rest = inner[(colon + 1)..].Replace(':', '.');
        if (!double.TryParse(rest, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return true;
    }
}
