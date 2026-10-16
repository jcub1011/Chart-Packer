using System.Globalization;

namespace ChartShared.Core.Metadata;

/// <summary>
/// How a song's background video lines up with its audio, from <c>video_start_time</c>,
/// <c>video_end_time</c> and <c>video_loop</c>.
/// </summary>
/// <param name="Start">
/// Where in the video the song starts: a positive start skips that much of the video, a negative
/// one holds the video's first frame for that long.
/// </param>
/// <param name="End">Where in the video playback ends, or <see langword="null"/> for the end of the video.</param>
/// <param name="Loop">Whether the video starts over from <paramref name="Start"/> when it reaches its end.</param>
public sealed record VideoTimeline(TimeSpan Start, TimeSpan? End, bool Loop)
{
    internal const string StartKey = "video_start_time";
    private const string EndKey = "video_end_time";
    private const string LoopKey = "video_loop";

    // Kept well inside TimeSpan, so adding a song time to it can't overflow.
    internal static readonly long MaxMilliseconds = TimeSpan.FromDays(365).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>No offset, no end and no loop: the video plays alongside the song.</summary>
    public static VideoTimeline Default { get; } = new(TimeSpan.Zero, null, false);

    /// <summary>
    /// The timeline <paramref name="pairs"/> set: the first of each key, matched ignoring case. A value
    /// that isn't valid is ignored, as is a <c>video_end_time</c> that's negative (-1 is "not set").
    /// </summary>
    public static VideoTimeline From(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        string? start = First(pairs, StartKey);
        string? end = First(pairs, EndKey);
        string? loop = First(pairs, LoopKey);

        return new VideoTimeline(
            ParseMilliseconds(start) is { } s ? TimeSpan.FromMilliseconds(s) : TimeSpan.Zero,
            ParseMilliseconds(end) is { } e && e >= 0 ? TimeSpan.FromMilliseconds(e) : null,
            loop is not null && (loop.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || loop.Trim() == "1"));
    }

    /// <summary>
    /// Where in the video to be at <paramref name="songTime"/>. Before the video starts, its first frame;
    /// past its end, its last frame, or, when it loops, the matching point of a later time through.
    /// </summary>
    /// <param name="duration">The video's length, or zero when it isn't known.</param>
    public TimeSpan Map(TimeSpan songTime, TimeSpan duration)
    {
        var time = songTime + Start;
        if (time <= TimeSpan.Zero)
            return TimeSpan.Zero;

        TimeSpan? end = (End, duration > TimeSpan.Zero) switch
        {
            ({ } e, true) => e < duration ? e : duration,
            ({ } e, false) => e,
            (null, true) => duration,
            (null, false) => null,
        };
        if (end is not { } last || time < last)
            return time;
        if (!Loop)
            return last;

        var loopStart = Start > TimeSpan.Zero ? Start : TimeSpan.Zero;
        var span = last - loopStart;
        if (span <= TimeSpan.Zero)
            return last;

        return loopStart + TimeSpan.FromTicks((time - loopStart).Ticks % span.Ticks);
    }

    private static string? First(IReadOnlyList<KeyValuePair<string, string>> pairs, string key)
    {
        foreach (var pair in pairs)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    /// <summary>A time in milliseconds as the timeline reads one, or <see langword="null"/> when it isn't one.</summary>
    internal static long? ParseMilliseconds(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && ms >= -MaxMilliseconds && ms <= MaxMilliseconds
            ? ms
            : null;
}
