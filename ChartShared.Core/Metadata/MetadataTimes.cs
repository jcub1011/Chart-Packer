using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ChartShared.Core.Metadata;

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

    /// Whether <paramref name="key"/> holds a time in milliseconds: a single value, or two for <c>preview</c>.
    public static bool IsTimeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return MillisecondKeys.ContainsKey(key) || key.Equals(PreviewPairKey, StringComparison.OrdinalIgnoreCase);
    }

    /// Whether <paramref name="key"/> holds a single time in milliseconds, which the playhead can set.
    public static bool IsSingleTimeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return MillisecondKeys.ContainsKey(key);
    }

    /// <summary>
    /// Normalises <paramref name="text"/> typed for <paramref name="key"/> to the milliseconds song.ini holds:
    /// a bare count stays one, and a timecode becomes one. Returns false when it is neither.
    /// </summary>
    /// <remarks>
    /// Timecodes are <c>HH:MM:SS:mmm</c>, with the same shorthands: <c>M:SS:mmm</c>, <c>M:SS.mmm</c> and
    /// <c>S.mmm</c>, and a bare <c>M:SS</c> reads as minutes and seconds. Three colon-separated parts read
    /// as minutes, seconds and milliseconds when the last part has three digits or is past 59, and as
    /// hours, minutes and seconds otherwise.
    /// </remarks>
    public static bool TryNormalize(string key, string text, [NotNullWhen(true)] out string? normalized)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);
        normalized = null;

        if (MillisecondKeys.TryGetValue(key, out bool allowNegative))
        {
            if (!TryParseTimecode(text, allowNegative, out long ms))
                return false;
            normalized = ms.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key.Equals(PreviewPairKey, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.Split([' ', '\t', ',', '–', '—'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryParseTimecode(parts[0], false, out long start) || !TryParseTimecode(parts[1], false, out long end))
                return false;
            normalized = start.ToString(CultureInfo.InvariantCulture) + " " + end.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="text"/> as milliseconds: a bare count, or a timecode as <see cref="TryNormalize"/>
    /// reads it. Returns false when it is neither, is negative where that isn't allowed, or runs past <see cref="long"/>.
    /// </summary>
    public static bool TryParseTimecode(string text, bool allowNegative, out long milliseconds)
    {
        ArgumentNullException.ThrowIfNull(text);
        milliseconds = 0;

        string rest = text.Trim();
        if (rest.Length == 0)
            return false;

        bool negative = false;
        if (rest[0] is '-' or '+')
        {
            negative = rest[0] == '-';
            rest = rest[1..].TrimStart();
            if (rest.Length == 0)
                return false;
        }

        if (!allowNegative && negative)
            return false;

        if (rest.IndexOfAny([':', '.']) < 0)
        {
            if (!long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out long bare) || bare < 0)
                return false;
            milliseconds = negative ? -bare : bare;
            return true;
        }

        string[] colonParts = rest.Split(':');
        foreach (string part in colonParts)
        {
            if (part.Length == 0)
                return false;
        }

        long fracMs = 0;
        bool hasFrac = false;
        string last = colonParts[^1];
        int dot = last.IndexOf('.');
        if (dot >= 0)
        {
            string seconds = last[..dot];
            string frac = last[(dot + 1)..];
            if (seconds.Length == 0 || frac is not { Length: >= 1 and <= 3 } || !IsDigits(seconds) || !IsDigits(frac))
                return false;
            if (!long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out long sec) || sec is < 0 or > 59)
                return false;
            colonParts[^1] = seconds;
            fracMs = long.Parse(frac.PadRight(3, '0'), CultureInfo.InvariantCulture);
            hasFrac = true;
        }

        foreach (string part in colonParts)
        {
            if (!IsDigits(part))
                return false;
        }

        long[] numbers = new long[colonParts.Length];
        for (int i = 0; i < colonParts.Length; i++)
        {
            if (!long.TryParse(colonParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        long hours = 0, minutes = 0, secondsPart = 0, msPart = fracMs;
        switch (colonParts.Length)
        {
            case 1 when hasFrac:
                secondsPart = numbers[0];
                break;
            case 2 when hasFrac:
                minutes = numbers[0];
                secondsPart = numbers[1];
                break;
            case 3 when hasFrac:
                hours = numbers[0];
                minutes = numbers[1];
                secondsPart = numbers[2];
                break;
            case 2:
                minutes = numbers[0];
                secondsPart = numbers[1];
                break;
            case 3:
                // Minutes, seconds and milliseconds, unless the last part reads as whole seconds.
                if (colonParts[2].Length == 3 || numbers[2] > 59)
                {
                    minutes = numbers[0];
                    secondsPart = numbers[1];
                    msPart = numbers[2];
                }
                else
                {
                    hours = numbers[0];
                    minutes = numbers[1];
                    secondsPart = numbers[2];
                }

                break;
            case 4 when !hasFrac:
                hours = numbers[0];
                minutes = numbers[1];
                secondsPart = numbers[2];
                msPart = numbers[3];
                break;
            default:
                return false;
        }

        if (minutes is < 0 or > 59 || secondsPart is < 0 or > 59 || msPart is < 0 or > 999 || hours < 0)
            return false;

        try
        {
            long total = checked(((hours * 60 + minutes) * 60 + secondsPart) * 1000 + msPart);
            milliseconds = negative ? -total : total;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// The value as the edit box should show it: the timecode for a time key that parses, otherwise
    /// <see langword="null"/>. The <c>preview</c> pair shows its halves space-separated, as typed.
    /// </summary>
    public static bool TryGetEditable(string key, string value, [NotNullWhen(true)] out string? editable)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        editable = null;

        if (MillisecondKeys.TryGetValue(key, out bool allowNegative))
        {
            if (!TryParse(value, allowNegative, out long ms))
                return false;
            editable = FormatMilliseconds(ms);
            return true;
        }

        if (key.Equals(PreviewPairKey, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryParse(parts[0], false, out long start) || !TryParse(parts[1], false, out long end))
                return false;
            editable = FormatMilliseconds(start) + " " + FormatMilliseconds(end);
            return true;
        }

        return false;
    }

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

    private static bool IsDigits(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
