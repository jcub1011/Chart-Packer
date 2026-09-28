using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ChartPacker.Core.Metadata;

/// <summary>
/// Recognises metadata keys whose values are times in milliseconds and formats them as
/// <c>HH:MM:SS:mmm</c>, so a bare <c>225123</c> reads as <c>00:03:45:123</c>.
/// </summary>
/// <remarks>
/// Keys and units come from the song.ini tag lists that sng-spec §6.3 names as the key registry
/// (TheNathannator/GuitarGame_ChartFormats: Standard Tags, plus Phase Shift's <c>preview</c> pair).
/// </remarks>
public static class MetadataTimes
{
    private const string PreviewPairKey = "preview";
    private const string PairSeparator = " – ";

    // Key → whether a negative value is meaningful. For the others a negative value isn't a time
    // (video_end_time uses -1 for "not set"), so it's left as stored.
    private static readonly Dictionary<string, bool> MillisecondKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["song_length"] = false,
        ["preview_start_time"] = false,
        ["preview_end_time"] = false,
        ["delay"] = true,
        ["video_start_time"] = true,
        ["video_end_time"] = false,
    };

    /// <summary>
    /// Formats <paramref name="value"/> when <paramref name="key"/> holds milliseconds and the value
    /// is a valid time for it. Returns false, leaving the value to be shown as stored, otherwise.
    /// </summary>
    public static bool TryFormat(string key, string value, [NotNullWhen(true)] out string? formatted)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        formatted = null;

        if (MillisecondKeys.TryGetValue(key, out bool allowNegative))
        {
            if (!TryParse(value, allowNegative, out long ms))
                return false;
            formatted = FormatMilliseconds(ms);
            return true;
        }

        if (key.Equals(PreviewPairKey, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryParse(parts[0], false, out long start) || !TryParse(parts[1], false, out long end))
                return false;
            formatted = FormatMilliseconds(start) + PairSeparator + FormatMilliseconds(end);
            return true;
        }

        return false;
    }

    /// <summary>Formats a millisecond count as <c>HH:MM:SS:mmm</c>, with a leading <c>-</c> when negative. Hours aren't capped at 24.</summary>
    public static string FormatMilliseconds(long milliseconds)
    {
        // Magnitude as ulong so long.MinValue doesn't overflow when negated.
        ulong total = milliseconds < 0 ? (ulong)(-(milliseconds + 1)) + 1 : (ulong)milliseconds;
        ulong ms = total % 1000;
        ulong seconds = total / 1000 % 60;
        ulong minutes = total / 60_000 % 60;
        ulong hours = total / 3_600_000;

        return string.Create(CultureInfo.InvariantCulture, $"{(milliseconds < 0 ? "-" : "")}{hours:00}:{minutes:00}:{seconds:00}:{ms:000}");
    }

    /// <summary>
    /// Formats a non-negative millisecond count as a compact duration, <c>M:SS</c> or <c>H:MM:SS</c>,
    /// dropping the milliseconds.
    /// </summary>
    public static string FormatDuration(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);

        long totalSeconds = milliseconds / 1000;
        long seconds = totalSeconds % 60;
        long minutes = totalSeconds / 60 % 60;
        long hours = totalSeconds / 3600;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    /// <summary>Parses an integer in the spec's form (§6.2): an optional sign, then digits.</summary>
    private static bool TryParse(string text, bool allowNegative, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && (allowNegative || value >= 0);
}
