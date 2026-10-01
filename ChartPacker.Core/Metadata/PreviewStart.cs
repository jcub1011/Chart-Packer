using System.Globalization;

namespace ChartPacker.Core.Metadata;

/// <summary>Where a song's <c>preview_start_time</c> says its preview begins.</summary>
public static class PreviewStart
{
    private const string Key = "preview_start_time";

    private static readonly long MaxMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// The start from <paramref name="pairs"/>: the first <c>preview_start_time</c>, matched ignoring case, as
    /// the metadata table shows it. Null when there isn't a valid one.
    /// </summary>
    public static TimeSpan? From(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        foreach (var pair in pairs)
        {
            if (pair.Key.Equals(Key, StringComparison.OrdinalIgnoreCase))
                return Parse(pair.Value);
        }

        return null;
    }

    /// <summary>A whole number of milliseconds, not negative and small enough to be a time; null otherwise.</summary>
    public static TimeSpan? Parse(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms >= 0 && ms <= MaxMilliseconds
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>
    /// Where to start playing: <paramref name="start"/>, or the beginning of the song when there is none or it
    /// isn't inside the song. A <paramref name="length"/> of zero means the song's length isn't known, which
    /// leaves <paramref name="start"/> as it is.
    /// </summary>
    public static TimeSpan Resolve(TimeSpan? start, TimeSpan length) =>
        start is { } at && (length <= TimeSpan.Zero || at < length) ? at : TimeSpan.Zero;
}
