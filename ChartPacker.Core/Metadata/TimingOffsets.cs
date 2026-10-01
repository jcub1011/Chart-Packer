using System.Globalization;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Metadata;

/// <summary>
/// The song.ini timing keys edited as offsets: how far a change moves the audio, or the background video,
/// later when positive, against the song as it's stored.
/// </summary>
/// <remarks>
/// <para>An offset isn't kept anywhere of its own; it's the difference the draft's change to the key makes, so
/// editing the key itself shows as an offset too. Moving something later lowers its key: <c>delay</c> plays the
/// chart that much later against the audio, so audio later is a smaller <c>delay</c>, and
/// <c>video_start_time</c> skips that much of the video, so a video later is a smaller start.</para>
/// <para>A key that isn't set, or isn't a valid number, counts as the value the song plays with without it:
/// for <c>delay</c> the chart's own offset, for <c>video_start_time</c> zero.</para>
/// </remarks>
public static class TimingOffsets
{
    /// <summary>The key the audio offset changes: the chart's delay against the audio.</summary>
    public const string AudioKey = "delay";

    /// <summary>The key the background video's offset changes: where in the video the song starts.</summary>
    public const string VideoKey = VideoTimeline.StartKey;

    /// <summary>
    /// How much later what <paramref name="key"/> places plays with <paramref name="drafted"/> than with
    /// <paramref name="original"/>, in milliseconds.
    /// </summary>
    /// <param name="fallback">What the key counts as when it isn't set: for <c>delay</c>, the chart's own offset.</param>
    public static int OffsetOf(string key, IReadOnlyList<KeyValuePair<string, string>> original, IReadOnlyList<KeyValuePair<string, string>> drafted, TimeSpan fallback)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(drafted);
        long offset = ValueOf(key, original, fallback) - ValueOf(key, drafted, fallback);
        return (int)Math.Clamp(offset, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// Changes <paramref name="key"/> in <paramref name="draft"/> so that it plays <paramref name="milliseconds"/>
    /// later than <paramref name="original"/> has it; zero forgets the change, leaving the key as it's stored.
    /// </summary>
    /// <param name="fallback">What the key counts as when it isn't set: for <c>delay</c>, the chart's own offset.</param>
    public static void Set(MetadataDraft draft, string key, IReadOnlyList<KeyValuePair<string, string>> original, TimeSpan fallback, int milliseconds)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(original);
        if (milliseconds == 0)
            draft.Revert(key);
        else
            draft.Set(key, (ValueOf(key, original, fallback) - milliseconds).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary><paramref name="key"/>'s value in <paramref name="pairs"/>, in milliseconds, read as the game reads it.</summary>
    private static long ValueOf(string key, IReadOnlyList<KeyValuePair<string, string>> pairs, TimeSpan fallback)
    {
        string? value = SongChartSettings.First(pairs, key);
        long? ms = key.Equals(AudioKey, StringComparison.OrdinalIgnoreCase) ? SongChartSettings.ParseDelay(value)
            : key.Equals(VideoKey, StringComparison.OrdinalIgnoreCase) ? VideoTimeline.ParseMilliseconds(value)
            : throw new ArgumentException($"'{key}' isn't a timing key.", nameof(key));
        return ms ?? (long)Math.Round(fallback.TotalMilliseconds);
    }
}
