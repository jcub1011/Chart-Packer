using System.Numerics;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// How a note is played: strummed, a hammer-on or pull-off, or tapped (docs/difficulty-model.md §2.3); for drums, hit,
/// or hit in a roll or trill lane.
/// </summary>
public enum NoteKind : byte
{
    /// <summary>Strummed; for drums, any hit outside a roll or trill lane.</summary>
    Strum,
    Hopo,
    Tap,

    /// <summary>A drum hit in a roll or trill lane, where the game counts what's hit however fast (§14.1).</summary>
    Roll,
}

/// <summary>
/// One note event of a track as the difficulty model reads it: a chord is one event (docs/difficulty-model.md §2).
/// </summary>
/// <param name="TimeMs">Its onset, in milliseconds from the chart's start.</param>
/// <param name="Beat">Its onset in beats (quarter notes), fractional.</param>
/// <param name="Lanes">
/// Its frets as bits, green (six-fret: the first white fret) the lowest; 0 for an open note. For drums, its pads as
/// <see cref="DrumPads"/> lays them out, the kick among them.
/// </param>
/// <param name="Kind">How it's played.</param>
/// <param name="Held">How many lanes not in it are still sustaining at its onset (χ); 0 for drums.</param>
public readonly record struct DifficultyNote(double TimeMs, double Beat, byte Lanes, NoteKind Kind, int Held)
{
    /// <summary>How many frets or pads it has: 0 for an open note.</summary>
    public int Count => BitOperations.PopCount(Lanes);
}

/// <summary>A track read into the difficulty model's note events (docs/difficulty-model.md §2 and §14).</summary>
public static class DifficultyNotes
{
    /// <summary>
    /// The note events of <paramref name="track"/>, read from the game view so natural HOPOs, forced flags, taps, the
    /// sustain cutoff and the drums' layout are as the games play them; notes after <paramref name="endTick"/> (the
    /// <c>[end]</c> event) are left out. <paramref name="parameters"/>' instrument says how the lanes are read.
    /// </summary>
    /// <remarks>
    /// <para>A chord with an open note and frets is read as its frets. A tap anywhere in a chord makes it a tap, then a
    /// HOPO a HOPO, as the flags are the chord's. Notes less than <see cref="DifficultyParameters.ChordMergeMs"/> after a
    /// chord are read as part of it, played as it is: a rolled or unquantized chord, or a flam, is played as one.</para>
    /// <para>For drums, Expert+ kicks aren't read, as <c>diff_drums</c> rates Expert, and a note in one of
    /// <paramref name="freeLanes"/>, the part's roll and trill lanes, is a <see cref="NoteKind.Roll"/>.</para>
    /// </remarks>
    public static IReadOnlyList<DifficultyNote> Of(NoteTrack track, TempoMap tempo, long? endTick = null, DifficultyParameters? parameters = null, IReadOnlyList<Phrase>? freeLanes = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(tempo);
        var p = parameters ?? DifficultyParameters.Default;
        var instrument = p.Instrument;
        bool drums = instrument.IsDrums();
        int laneCount = instrument == DifficultyInstrument.SixFret ? 6 : 5;
        var notes = new List<DifficultyNote>();
        double resolution = tempo.Resolution;

        // Where each lane's sustain ends, in ticks; long.MinValue for none.
        Span<long> holds = stackalloc long[6];
        holds.Fill(long.MinValue);
        var source = track.Notes;
        for (int i = 0; i < source.Count;)
        {
            long tick = source[i].Tick;
            if (endTick is { } end && tick > end)
                break;
            double timeMs = tempo.SecondsAt(tick) * 1000;
            byte lanes = 0;
            var flags = NoteFlags.None;
            int j = i;
            for (; j < source.Count && (source[j].Tick == tick || IsRolled(source[j].Tick)); j++)
            {
                var note = source[j];
                if (note.Tick == tick)
                    flags |= note.Flags;
                if (drums)
                    lanes |= DrumPads.BitOf(note, instrument);
                else if (note.Lane >= 0 && note.Lane < laneCount)
                    lanes |= (byte)(1 << note.Lane);
            }

            if (drums)
            {
                // A tick of Expert+ kicks alone isn't played on Expert.
                if (lanes != 0)
                    notes.Add(new DifficultyNote(timeMs, tick / resolution, lanes, InFreeLane(tick) ? NoteKind.Roll : NoteKind.Strum, 0));
                i = j;
                continue;
            }

            int held = 0;
            for (int lane = 0; lane < laneCount; lane++)
            {
                if ((lanes & (1 << lane)) == 0 && holds[lane] > tick)
                    held++;
            }

            for (int k = i; k < j; k++)
            {
                var note = source[k];
                if (note.Length > 0 && note.Lane >= 0 && note.Lane < laneCount && (lanes & (1 << note.Lane)) != 0)
                    holds[note.Lane] = Math.Max(holds[note.Lane], note.Tick + note.Length);
            }

            var kind = (flags & NoteFlags.Tap) != 0 ? NoteKind.Tap : (flags & NoteFlags.Hopo) != 0 ? NoteKind.Hopo : NoteKind.Strum;
            notes.Add(new DifficultyNote(timeMs, tick / resolution, lanes, kind, held));
            i = j;

            // A later note close enough to the chord's to be played with it, and not past the end.
            bool IsRolled(long at) => p.ChordMergeMs > 0 && !(endTick is { } last && at > last) && (tempo.SecondsAt(at) * 1000) - timeMs < p.ChordMergeMs;
        }

        bool InFreeLane(long at)
        {
            if (freeLanes is null)
                return false;
            foreach (var lane in freeLanes)
            {
                if (lane.Covers(at))
                    return true;
            }

            return false;
        }

        double offset = GridOffset(notes.Select(static n => n.Beat).ToList(), p.BeatTolerance);
        if (offset != 0)
        {
            for (int k = 0; k < notes.Count; k++)
                notes[k] = notes[k] with { Beat = notes[k].Beat - offset };
        }

        return notes;
    }

    /// <summary>The furthest the notes' grid is looked for away from the tempo map's, in beats: a 32nd note.</summary>
    public const double MaxGridOffset = 1.0 / 32;

    /// <summary>
    /// How far, in beats, the notes sit from the tempo map's beats and half-beats, when most of them sit the same small
    /// distance off: a chart whose notes were moved a few ticks to meet the audio, rather than its tempo map. Zero when
    /// the notes are on the grid, or no offset puts clearly more of them on it.
    /// </summary>
    /// <remarks>
    /// The rhythm terms that read beat positions (docs/difficulty-model.md §4.3) assume the notes are on the tempo map's
    /// grid. A player can't hear an offset the whole chart shares, so it's taken out: otherwise every note would be off
    /// every grid and syncopated. Only offsets up to <see cref="MaxGridOffset"/> are looked for, so a riff on the
    /// off-beats is never taken for one on the beat.
    /// </remarks>
    public static double GridOffset(IReadOnlyList<double> beats, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count == 0)
            return 0;

        // How far each note is from its nearest beat or half-beat, of those near enough to be offset ones.
        var off = new List<double>(beats.Count);
        int onGrid = 0;
        foreach (double beat in beats)
        {
            double distance = beat - (Math.Round(2 * beat) / 2);
            if (Math.Abs(distance) < tolerance)
                onGrid++;
            if (Math.Abs(distance) <= MaxGridOffset + tolerance)
                off.Add(distance);
        }

        off.Sort();

        // The densest stretch of offsets two tolerances wide.
        int best = 0, bestStart = 0;
        for (int start = 0, end = 0; start < off.Count; start++)
        {
            while (end < off.Count && off[end] - off[start] < 2 * tolerance)
                end++;
            if (end - start > best)
                (best, bestStart) = (end - start, start);
        }

        // Taken only when it puts clearly more notes on the grid than the tempo map does, and a good share of them.
        if (best < Math.Max(2, beats.Count / 4) || best <= onGrid * 1.25)
            return 0;
        double offset = off[bestStart + (best / 2)];
        return Math.Abs(offset) <= MaxGridOffset ? offset : 0;
    }

    /// <summary>
    /// The roll and trill lanes of <paramref name="part"/> that reach <paramref name="difficulty"/>, as
    /// <see cref="Of"/> takes them; none for a part that isn't drums.
    /// </summary>
    public static IReadOnlyList<Phrase> FreeLanesOf(EditablePart part, ChartDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Kind != ChartInstrumentKind.Drums)
            return [];
        return [.. part.Phrases.Where(p => p.Kind is PhraseKind.Roll or PhraseKind.Trill && (p.LowestDifficulty ?? ChartDifficulty.Expert) <= difficulty)];
    }

    /// <summary>The tick of <paramref name="chart"/>'s <c>[end]</c> event, which the games stop reading at; null for none.</summary>
    public static long? EndTickOf(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        foreach (var e in chart.Events)
        {
            if (SongEvents.KindOf(e.Text) == SongEvent.End)
                return e.Tick;
        }

        return null;
    }
}
