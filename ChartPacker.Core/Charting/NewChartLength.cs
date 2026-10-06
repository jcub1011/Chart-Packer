using System.Globalization;

namespace ChartPacker.Core.Charting;

/// <summary>The length a new chart without audio is given, as the wizard has it typed (charter-spec C1, Audio).</summary>
public static class NewChartLength
{
    /// <summary>The length a chart without audio starts with.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromMinutes(3);

    /// <summary>The longest a chart can be: as long as <c>delay</c> can reach, just under a day.</summary>
    public static TimeSpan Max { get; } = TimeSpan.FromMilliseconds(NewChartRequest.MaxLeadInMs);

    /// <summary>
    /// Reads <paramref name="text"/> as <c>M:SS</c> or <c>H:MM:SS</c>, or as whole seconds, e.g.
    /// <c>3:30</c>, <c>1:02:00</c> or <c>210</c>. Seconds can have a fraction.
    /// </summary>
    /// <returns>Whether it's a length longer than zero and no longer than <see cref="Max"/>.</returns>
    public static bool TryParse(string? text, out TimeSpan length)
    {
        length = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Split(':');
        if (parts.Length > 3)
            return false;

        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
            return false;

        // Minutes and seconds after a colon are under 60; the first part can be as large as it likes.
        if (parts.Length > 1 && seconds >= 60)
            return false;

        double total = seconds;
        int scale = 60;
        for (int i = parts.Length - 2; i >= 0; i--, scale *= 60)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return false;
            if (i > 0 && value >= 60)
                return false;
            total += (double)value * scale;
        }

        if (!(total > 0) || total * 1000 > Max.TotalMilliseconds)
            return false;

        length = TimeSpan.FromMilliseconds(Math.Round(total * 1000));
        return true;
    }

    /// <summary><paramref name="length"/> as <see cref="TryParse"/> reads it: <c>M:SS</c>, or <c>H:MM:SS</c> from an hour.</summary>
    public static string Format(TimeSpan length) => SongTime.Format(length);
}
