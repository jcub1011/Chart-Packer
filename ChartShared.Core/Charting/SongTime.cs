using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChartShared.Core.Charting;

/// <summary>A time in a song, written the way the editor and its timeline show it.</summary>
public static class SongTime
{
    /// <summary>
    /// <paramref name="time"/> as <c>M:SS</c>, or <c>H:MM:SS</c> from an hour, with <paramref name="decimals"/>
    /// places of a second, which are cut off rather than rounded, e.g. <c>3:05.2</c> for 3:05.29 with one place.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="decimals"/> isn't 0 to 7.</exception>
    public static string Format(TimeSpan time, int decimals = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimals, 7);

        // Written straight into the one string, as the playhead's time is many times a second while a song plays.
        bool hours = time.TotalHours >= 1;
        var text = new DefaultInterpolatedStringHandler(0, 2, CultureInfo.InvariantCulture);
        text.AppendFormatted(hours ? (int)time.TotalHours : (int)time.TotalMinutes);
        text.AppendFormatted(time, (hours ? HourFormats : MinuteFormats)[decimals]);
        return text.ToStringAndClear();
    }

    // The rest of the time after its hours, or its minutes, with each number of decimals: worked out once, not for each time.
    private static readonly string[] HourFormats = FormatsAfter(@"\:mm\:ss");
    private static readonly string[] MinuteFormats = FormatsAfter(@"\:ss");

    private static string[] FormatsAfter(string whole)
    {
        var formats = new string[8];
        for (int decimals = 0; decimals < formats.Length; decimals++)
            formats[decimals] = decimals > 0 ? whole + @"\." + new string('f', decimals) : whole;
        return formats;
    }
}
