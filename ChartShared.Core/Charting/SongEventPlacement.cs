using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting;

/// <summary>Where the song's audio is heard: from its first sound to its last, in seconds of audio time.</summary>
public readonly record struct AudibleRange(double StartSeconds, double EndSeconds);

/// <summary>
/// Where Chart Builder puts the song events it places itself (charter-spec C8 item 5): <c>[music_start]</c>,
/// <c>[music_end]</c> and <c>[end]</c>, in ticks.
/// </summary>
/// <param name="FromAudio">Whether the music's start and end were found in the audio, rather than worked out from the notes.</param>
public sealed record SongEventMarks(long MusicStart, long MusicEnd, long End, bool FromAudio)
{
    /// <summary>The tick of <paramref name="songEvent"/>, one of <see cref="SongEventPlacement.Placed"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="songEvent"/> isn't placed.</exception>
    public long TickOf(SongEvent songEvent) => songEvent switch
    {
        SongEvent.MusicStart => MusicStart,
        SongEvent.MusicEnd => MusicEnd,
        SongEvent.End => End,
        _ => throw new ArgumentOutOfRangeException(nameof(songEvent), songEvent, "Only [music_start], [music_end] and [end] are placed."),
    };
}

/// <summary>
/// Places <c>[music_start]</c>, <c>[music_end]</c> and <c>[end]</c> (charter-spec C8 item 5). They're worked out from the
/// audio and the notes whenever either changes, never held in the model the editor edits, and put in only what's written.
/// </summary>
/// <remarks>
/// <para><c>[music_start]</c> is the audio's first sound, but no later than the first note; with no audio, the measure line
/// at or before the first note, or tick 0 with no notes.</para>
/// <para><c>[music_end]</c> is the audio's last sound, but no earlier than the last note's end; with no audio, the last
/// note's end.</para>
/// <para><c>[end]</c> is the first measure line after both.</para>
/// <para>The model never holds them because commands undo by their place in <see cref="EditableChart.Events"/>: moving
/// them outside the undo stack would shift what a later undo takes back.</para>
/// </remarks>
public static class SongEventPlacement
{
    /// <summary>The song events placed, in the order they're written.</summary>
    public static IReadOnlyList<SongEvent> Placed { get; } = [SongEvent.MusicStart, SongEvent.MusicEnd, SongEvent.End];

    /// <summary>Whether <paramref name="songEvent"/> is placed rather than edited: all but <c>[coda]</c>.</summary>
    public static bool IsPlaced(SongEvent songEvent) => songEvent != SongEvent.Coda;

    /// <summary>Whether <paramref name="e"/> is one the games read as a placed song event, however it's spaced.</summary>
    public static bool IsPlaced(TextEvent e) =>
        MidiChartLoader.IsReadText(e.Type) && SongEvents.KindOf(e.Text) is { } kind && IsPlaced(kind);

    /// <summary>
    /// Takes every placed song event out of <paramref name="chart"/>'s <c>EVENTS</c>, as the editor takes up a chart, before
    /// it has an undo stack. Duplicates go too.
    /// </summary>
    /// <returns>The events taken out, in their order.</returns>
    public static IReadOnlyList<TextEvent> Strip(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var stripped = new List<TextEvent>();
        for (int i = chart.Events.Count - 1; i >= 0; i--)
        {
            if (IsPlaced(chart.Events[i]))
            {
                stripped.Add(chart.Events[i]);
                chart.Events.RemoveAt(i);
            }
        }

        stripped.Reverse();
        return stripped;
    }

    /// <summary>Where the placed song events go in <paramref name="chart"/>.</summary>
    /// <param name="audio">Where the audio is heard; null with no audio, or none of it heard, to work it out from the notes.</param>
    /// <param name="delay">The song's delay: audio time is chart time plus this.</param>
    /// <param name="vocals">The first vocal note's tick and the last one's end, which count as notes; null for none.</param>
    public static SongEventMarks Place(EditableChart chart, AudibleRange? audio, TimeSpan delay, (long First, long End)? vocals = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap.ToTempoMap();
        long? first = Earlier(FirstNoteTick(chart), vocals?.First);
        long? last = Later(LastNoteEnd(chart), vocals?.End);

        long musicStart, musicEnd;
        if (audio is { } heard)
        {
            // A hair either way, so a sound that starts or ends on a tick isn't put a tick off it.
            musicStart = Math.Max(0, (long)Math.Floor(map.TickAt(heard.StartSeconds - delay.TotalSeconds) + 1e-6));
            musicEnd = (long)Math.Ceiling(map.TickAt(heard.EndSeconds - delay.TotalSeconds) - 1e-6);
            if (first is { } f)
                musicStart = Math.Min(musicStart, f);
        }
        else
        {
            musicStart = first is { } f ? MeasureBeat.MeasureStart(map, f) : 0;
            musicEnd = 0;
        }

        musicEnd = Math.Max(Math.Max(musicEnd, last ?? 0), musicStart);
        return new SongEventMarks(musicStart, musicEnd, MeasureLineAfter(map, musicEnd), audio is not null);
    }

    /// <summary>
    /// A copy of <paramref name="chart"/> to write, with one of each placed song event at <paramref name="marks"/> in place
    /// of any it has.
    /// </summary>
    public static EditableChart WithSongEvents(EditableChart chart, SongEventMarks marks)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(marks);
        var copy = chart.Clone();
        Strip(copy);
        foreach (var kind in Placed)
            copy.Events.Add(TextEvent.Of(marks.TickOf(kind), SongEvents.TextOf(kind)));
        return copy;
    }

    /// <summary>
    /// Whether <paramref name="events"/>, a chart's placed song events, are one of each at <paramref name="marks"/>, in
    /// whatever order they're written at one tick.
    /// </summary>
    public static bool Matches(IReadOnlyList<TextEvent> events, SongEventMarks marks)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(marks);
        if (events.Count != Placed.Count)
            return false;
        foreach (var kind in Placed)
        {
            if (!events.Any(e => e.Tick == marks.TickOf(kind) && e.Text == SongEvents.TextOf(kind)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Where the last note in any part ends: a sustain's end, or a plain note's tick. Null when the chart has no notes.
    /// Vocals aren't parts, so they don't count.
    /// </summary>
    public static long? LastNoteEnd(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        long? last = null;
        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                foreach (var note in track.Notes.AsSpan())
                {
                    long noteEnd = note.Tick + (note.Length > EditableNote.PlainLength ? note.Length : 0);
                    if (last is null || noteEnd > last)
                        last = noteEnd;
                }
            }
        }

        return last;
    }

    /// <summary>The tick of the first note in any part; null when the chart has no notes. Vocals don't count.</summary>
    public static long? FirstNoteTick(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        long? first = null;
        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count > 0 && (first is null || track.Notes[0].Tick < first))
                    first = track.Notes[0].Tick;
            }
        }

        return first;
    }

    /// <summary>The first measure line after <paramref name="tick"/>; or a measure of 4/4 on, past the tempo map's lines.</summary>
    public static long MeasureLineAfter(TempoMap map, long tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        long horizon = tick + (8L * map.Resolution * 64);
        foreach (var line in map.BeatLines(tick + 1, horizon))
        {
            if (line.IsMeasure)
                return line.Tick;
        }

        return tick + (4L * map.Resolution);
    }

    private static long? Earlier(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static long? Later(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
