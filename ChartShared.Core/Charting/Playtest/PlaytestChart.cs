using System.Numerics;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Playtest;

/// <summary>How a note is played (charter-spec C19 items 5 and 6).</summary>
public enum PlayNoteKind
{
    /// <summary>A 5-fret note that needs a strum.</summary>
    Strum,

    /// <summary>A 5-fret hammer-on or pull-off: hit by the frets while the combo is going.</summary>
    Hopo,

    /// <summary>A 5-fret tap: hit by the frets at any time.</summary>
    Tap,

    /// <summary>A drum note.</summary>
    Pad,
}

/// <summary>
/// One thing to hit: a 5-fret chord (or single note) at one tick, or one drum note. Lanes are the game view's
/// (<see cref="ChartNote.Lane"/>): frets 0 to 4, drum pads from red, <see cref="ChartNote.OpenLane"/> an open note or
/// the kick.
/// </summary>
/// <param name="Index">Its place in <see cref="PlaytestChart.Notes"/>.</param>
/// <param name="Time">When it's hit, in audio seconds.</param>
/// <param name="EndTime">When its sustain ends; <paramref name="Time"/> for none.</param>
/// <param name="Mask">For frets, a bit for each fret, 0 for an open note.</param>
/// <param name="Lanes">Its lanes, lowest first: one for a drum note or a single note.</param>
/// <param name="Cymbal">For drums, whether it's a cymbal.</param>
/// <param name="Flags">The flags of its first note: star power, a drum's accent or ghost, the 2× kick.</param>
public sealed record PlayNote(int Index, long Tick, double Time, double EndTime, PlayNoteKind Kind, int Mask, IReadOnlyList<int> Lanes, bool Cymbal, NoteFlags Flags)
{
    /// <summary>A drum note's lane.</summary>
    public int Lane => Lanes[0];

    public bool IsOpen => Kind != PlayNoteKind.Pad && Mask == 0;

    public bool IsChord => BitOperations.PopCount((uint)Mask) > 1;

    public bool HasSustain => EndTime > Time;
}

/// <summary>
/// A track's notes as playtest judges them (charter-spec C19 item 4): its game view (charter-spec §4.3), so natural
/// HOPOs, sustain cutoffs, cymbals and disco flips are as the games read them, with each note's time in audio seconds.
/// </summary>
public sealed class PlaytestChart
{
    private readonly Func<double, double> _tickAt;

    private PlaytestChart(ChartInstrumentKind kind, DrumsVariant drums, IReadOnlyList<PlayNote> notes, Func<double, double> tickAt)
    {
        Kind = kind;
        Drums = drums;
        Notes = notes;
        _tickAt = tickAt;
    }

    public ChartInstrumentKind Kind { get; }

    /// <summary>How the drums are laid out; for frets, unused.</summary>
    public DrumsVariant Drums { get; }

    /// <summary>The notes in time order, then lane order.</summary>
    public IReadOnlyList<PlayNote> Notes { get; }

    /// <summary>The tick at <paramref name="audioSeconds"/>.</summary>
    public double TickAt(double audioSeconds) => _tickAt(audioSeconds);

    /// <summary>The index of the first note at or after <paramref name="audioSeconds"/>; <see cref="Notes"/>' count for none.</summary>
    public int IndexAtOrAfter(double audioSeconds)
    {
        int lo = 0, hi = Notes.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Notes[mid].Time < audioSeconds)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>A 5-fret track's game view as chords: the notes at each tick are one, a HOPO or tap if any of them is.</summary>
    /// <param name="audioAt">A tick's time in audio seconds (<see cref="Editing.EditorSession.AudioAt"/>).</param>
    /// <param name="tickAt">An audio time's tick (<see cref="Editing.EditorSession.TickAt"/>).</param>
    public static PlaytestChart Frets(NoteTrack game, Func<double, double> audioAt, Func<double, double> tickAt)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(audioAt);
        ArgumentNullException.ThrowIfNull(tickAt);

        var notes = new List<PlayNote>();
        var source = game.Notes;
        for (int i = 0; i < source.Count;)
        {
            long tick = source[i].Tick;
            int mask = 0;
            long length = 0;
            var flags = NoteFlags.None;
            var lanes = new List<int>();
            for (; i < source.Count && source[i].Tick == tick; i++)
            {
                var note = source[i];
                if (note.Lane >= 0)
                    mask |= 1 << note.Lane;
                lanes.Add(note.Lane);
                length = Math.Max(length, note.Length);
                flags |= note.Flags;
            }

            // An open note with frets on its tick isn't readable; the frets are what's played.
            if (mask != 0)
                lanes.Remove(ChartNote.OpenLane);
            lanes.Sort();
            var kind = flags.HasFlag(NoteFlags.Tap) ? PlayNoteKind.Tap : flags.HasFlag(NoteFlags.Hopo) ? PlayNoteKind.Hopo : PlayNoteKind.Strum;
            double time = audioAt(tick);
            notes.Add(new PlayNote(notes.Count, tick, time, length > 0 ? audioAt(tick + length) : time, kind, mask, lanes, false, flags));
        }

        return new PlaytestChart(ChartInstrumentKind.FiveFret, DrumsVariant.FourLane, notes, tickAt);
    }

    /// <summary>A drums track's game view, each note on its own.</summary>
    /// <param name="variant">How the drums are laid out, as the game view reads them.</param>
    /// <param name="includeDoubleKick">Whether Expert's 2× kick notes are kept.</param>
    public static PlaytestChart DrumNotes(NoteTrack game, Func<double, double> audioAt, Func<double, double> tickAt, DrumsVariant variant, bool includeDoubleKick)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(audioAt);
        ArgumentNullException.ThrowIfNull(tickAt);

        var notes = new List<PlayNote>(game.Notes.Count);
        foreach (var note in game.Notes)
        {
            if (!includeDoubleKick && note.Flags.HasFlag(NoteFlags.DoubleKick))
                continue;
            double time = audioAt(note.Tick);
            notes.Add(new PlayNote(notes.Count, note.Tick, time, time, PlayNoteKind.Pad, 0, [note.Lane], note.Flags.HasFlag(NoteFlags.Cymbal), note.Flags));
        }

        return new PlaytestChart(ChartInstrumentKind.Drums, variant, notes, tickAt);
    }
}
