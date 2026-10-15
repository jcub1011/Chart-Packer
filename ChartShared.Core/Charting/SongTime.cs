using System.Globalization;

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

        string fraction = decimals > 0 ? @"\." + new string('f', decimals) : "";
        return time.TotalHours >= 1
            ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:mm\:ss" + fraction, CultureInfo.InvariantCulture)
            : ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:ss" + fraction, CultureInfo.InvariantCulture);
    }
}
