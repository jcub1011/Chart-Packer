using System.Globalization;

namespace ChartShared.Core.Lyrics;

/// <summary>Reading and writing the clock times lyric formats have.</summary>
internal static class LyricTime
{
    /// <summary>The time <paramref name="digits"/> are the decimal fraction of a second of: <c>5</c>, <c>50</c> and <c>500</c> are half a second.</summary>
    public static TimeSpan Fraction(ReadOnlySpan<char> digits)
    {
        if (digits.IsEmpty || digits.Length > 9 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(value / Math.Pow(10, digits.Length));
    }

    /// <summary>
    /// Reads <c>[hh:]mm:ss</c> with an optional <c>.fff</c> or <c>,fff</c>, as SRT and WebVTT write a time.
    /// </summary>
    public static bool TryParseClock(ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = default;
        text = text.Trim();
        int split = text.LastIndexOfAny('.', ',');
        var whole = split < 0 ? text : text[..split];
        var fraction = split < 0 ? default : text[(split + 1)..];

        Span<Range> parts = stackalloc Range[4];
        int count = whole.Split(parts, ':');
        if (count is < 2 or > 3)
            return false;

        double total = 0;
        for (int i = 0; i < count; i++)
        {
            var part = whole[parts[i]];
            if (part.IsEmpty || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return false;
            total = (total * 60) + value;
        }

        if (!fraction.IsEmpty && (fraction.Length > 9 || !IsDigits(fraction)))
            return false;
        time = TimeSpan.FromSeconds(total) + Fraction(fraction);
        return true;
    }

    /// <summary><c>mm:ss.xx</c>, as LRC writes a time, rounded to the hundredth; minutes go past 59 rather than becoming hours.</summary>
    public static string Lrc(double seconds)
    {
        long hundredths = (long)Math.Round(Math.Max(0, seconds) * 100, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{hundredths / 6000:00}:{hundredths / 100 % 60:00}.{hundredths % 100:00}");
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }
}
