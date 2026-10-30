using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Metadata;

/// <summary>Reads how long a background video is. Core can't decode video, so the app supplies this.</summary>
public interface IVideoDurationProbe
{
    /// <summary>The length of the video in <paramref name="stream"/>, or <see langword="null"/> when it can't be read.</summary>
    /// <param name="stream">The video, open for reading and seeking.</param>
    /// <param name="name">Its file name, for its extension.</param>
    TimeSpan? Duration(Stream stream, string name);
}

/// <summary>What a song's <c>song_length</c> should be, from each of the things that can run longest.</summary>
/// <param name="ChartEndMs">When the chart's last note ends, sustain included, across every instrument and difficulty.</param>
/// <param name="AudioMs">The length of the longest audio track, the preview left out.</param>
/// <param name="VideoEndMs">When the background video ends against the audio; <see langword="null"/> when it loops.</param>
public sealed record SongLengthEstimate(long? ChartEndMs, long? AudioMs, long? VideoEndMs)
{
    /// <summary>The longest part, or <see langword="null"/> when no part is known.</summary>
    public long? SongLengthMs => new[] { ChartEndMs, AudioMs, VideoEndMs }.Max();
}

/// <summary>
/// Works out a song's length from its chart, audio and background video, as the song will be once
/// its draft is saved: staged files count, and files the draft removes don't.
/// </summary>
public static class SongLengthEstimator
{
    /// <summary>Estimates the length of <paramref name="entry"/>.</summary>
    /// <param name="entry">The song.</param>
    /// <param name="draft">Its unsaved changes.</param>
    /// <param name="pairs">Its metadata with the draft applied, for the chart delay and the video timeline.</param>
    /// <param name="video">Reads the background video's length.</param>
    /// <param name="cancellationToken">Cancels the estimate.</param>
    /// <remarks>A part that can't be read is left out rather than failing the rest.</remarks>
    /// <exception cref="IOException">The song's files can't be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the song is denied.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    public static SongLengthEstimate Estimate(
        SongEntry entry,
        MetadataDraft draft,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IVideoDurationProbe video,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(video);

        var stored = SongFile.ListTopLevel(entry, cancellationToken).Select(f => f.Name).ToList();
        return new SongLengthEstimate(
            ChartEnd(entry, draft, pairs, stored, cancellationToken),
            LongestAudio(entry, draft, stored, cancellationToken),
            VideoEnd(entry, draft, pairs, stored, video, cancellationToken));
    }

    /// <summary>
    /// When a video of <paramref name="duration"/> stops playing against the audio, or
    /// <see langword="null"/> when it loops (it has no end) or is over before the song starts.
    /// </summary>
    public static long? VideoEndMs(VideoTimeline timeline, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Loop || duration <= TimeSpan.Zero)
            return null;

        var end = timeline.End is { } e && e < duration ? e : duration;
        var songEnd = end - timeline.Start;
        return songEnd > TimeSpan.Zero ? (long)Math.Ceiling(songEnd.TotalMilliseconds) : null;
    }

    private static long? LongestAudio(SongEntry entry, MetadataDraft draft, IReadOnlyList<string> stored, CancellationToken cancellationToken) =>
        SongAudioLength.LongestMs(entry, SongTracks.Resolve(stored, draft), cancellationToken);

    private static long? ChartEnd(SongEntry entry, MetadataDraft draft, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<string> stored, CancellationToken cancellationToken)
    {
        if (DraftChart.Of(draft, stored) is not { } picked)
            return null;

        try
        {
            byte[]? bytes = picked.ReadAsync(entry, cancellationToken).GetAwaiter().GetResult();
            if (bytes is null)
                return null;

            var options = ChartReadOptions.From(pairs);
            var chart = draft.HasChartOffsets
                ? ChartShifter.Shift(bytes, picked.Name, draft.ChartOffsets, options).Document
                : ChartReader.Read(bytes, picked.Name, options);
            return LastNoteEndMs(chart, SongChartSettings.DelayOf(pairs, chart));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ChartFormatException is an IOException: a malformed chart just doesn't count.
            return null;
        }
    }

    /// <summary>When the last note of any track ends, sustain included, against the audio; as the highway places it.</summary>
    private static long? LastNoteEndMs(ChartDocument chart, TimeSpan delay)
    {
        long? lastTick = null;
        foreach (var track in chart.Tracks.Values)
        {
            foreach (var note in track.Notes)
                lastTick = Math.Max(lastTick ?? 0, note.Tick + note.Length);
        }

        if (lastTick is not { } tick)
            return null;

        double ms = (chart.Tempo.SecondsAt(tick) * 1000) + delay.TotalMilliseconds;
        return ms > 0 ? (long)Math.Ceiling(ms) : null;
    }

    private static long? VideoEnd(SongEntry entry, MetadataDraft draft, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<string> stored, IVideoDurationProbe video, CancellationToken cancellationToken)
    {
        var timeline = VideoTimeline.From(pairs);
        if (timeline.Loop)
            return null;

        string? name;
        string? stagedPath = null;
        if (draft.BackgroundVideo is { } change)
        {
            // A change with no source removes the video.
            if (change.SourcePath is null)
                return null;
            stagedPath = change.SourcePath;
            name = Path.GetFileName(stagedPath);
        }
        else
        {
            name = SongBackgrounds.Pick(stored.Where(n => !draft.IsFileRemoved(n)).ToList(), n => n, BackgroundKind.Video);
            if (name is null)
                return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = Open(entry, name, stagedPath, cancellationToken);
            return stream is not null && video.Duration(stream, name) is { } duration ? VideoEndMs(timeline, duration) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Stream? Open(SongEntry entry, string storedName, string? stagedPath, CancellationToken cancellationToken) =>
        stagedPath is not null
            ? new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024)
            : SongFile.OpenRead(entry, storedName, cancellationToken);
}
