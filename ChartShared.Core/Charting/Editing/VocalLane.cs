using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>A sung note as a vocals lane draws it.</summary>
/// <param name="Pitch">The MIDI pitch, 36 to 84; not where an unpitched note is drawn.</param>
/// <param name="IsUnpitched">The note is spoken or shouted rather than sung (<c>#</c>, <c>^</c>), so it has no pitch to draw at.</param>
/// <param name="IsSlide">The note's lyric is <c>+</c>: it carries the note before it on to its pitch, and is drawn joined to it.</param>
public readonly record struct VocalLaneNote(long Tick, long EndTick, int Pitch, bool IsUnpitched, bool IsSlide);

/// <summary>A syllable as a vocals lane's lyrics show it.</summary>
/// <param name="JoinsNext">The next syllable follows on without a space.</param>
public readonly record struct VocalLaneLyric(long Tick, string Text, bool JoinsNext);

/// <summary>
/// One vocal part as an editor lane shows it: its notes with their syllables worked out, the syllables it shows, its
/// phrases, and the pitches its rows are. Everything is in ticks, as the chart has it, so the lane's projection places
/// it, and the pointer is mapped back to a tick and a pitch.
/// </summary>
/// <remarks>Read from the passthrough track by <see cref="EditorSession.VocalsOf"/>; it never changes.</remarks>
public sealed class VocalLane
{
    /// <summary>The pitch whose row a note that isn't sung is drawn along: middle C, the middle of <see cref="Pitches"/>.</summary>
    public const int SpokenPitch = 60;

    // Every pitch a part sings, from the highest down.
    private static readonly int[] AllPitches = [.. Enumerable.Range(0, VocalTrackContent.HighestPitch - VocalTrackContent.LowestPitch + 1).Select(static i => VocalTrackContent.HighestPitch - i)];

    public VocalLane(VocalPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        TrackName = part.TrackName;

        var syllables = new Dictionary<long, LyricSyllable>();
        foreach (var lyric in part.Lyrics)
            syllables.TryAdd(lyric.Tick, LyricSyllable.Of(lyric.Text));

        var notes = new VocalLaneNote[part.Notes.Count];
        for (int i = 0; i < notes.Length; i++)
        {
            var note = part.Notes[i];
            var syllable = syllables.GetValueOrDefault(note.Tick);
            notes[i] = new VocalLaneNote(note.Tick, note.Tick + note.Length, note.Pitch, syllable.IsUnpitched, syllable.IsSlide);
            LongestNote = Math.Max(LongestNote, note.Length);
        }

        Notes = notes;
        Lyrics = [.. part.Lyrics
            .Select(l => (l.Tick, Syllable: LyricSyllable.Of(l.Text)))
            .Where(l => !l.Syllable.IsSlide && l.Syllable.Text.Length > 0)
            .Select(l => new VocalLaneLyric(l.Tick, l.Syllable.Text, l.Syllable.JoinsNext))];
        Phrases = part.Phrases;

        var pitched = notes.Where(static n => !n.IsUnpitched).Select(static n => n.Pitch).ToList();
        (LowestPitch, HighestPitch) = pitched.Count > 0 ? (pitched.Min(), pitched.Max()) : (SpokenPitch, SpokenPitch);
    }

    /// <summary>The MIDI track the part was read from, e.g. <c>PART VOCALS</c>.</summary>
    public string TrackName { get; }

    /// <summary>The sung notes, in tick order.</summary>
    public IReadOnlyList<VocalLaneNote> Notes { get; }

    /// <summary>The syllables shown under the notes, in tick order: slides and empty syllables left out.</summary>
    public IReadOnlyList<VocalLaneLyric> Lyrics { get; }

    /// <summary>The phrases, in tick order.</summary>
    public IReadOnlyList<VocalPhrase> Phrases { get; }

    /// <summary>The lowest pitched note's pitch; middle C when there's none.</summary>
    public int LowestPitch { get; }

    /// <summary>The highest pitched note's pitch; middle C when there's none.</summary>
    public int HighestPitch { get; }

    /// <summary>
    /// The pitches the lane's rows are, from the highest down: every pitch a part sings, 36 to 84, whatever its notes, so
    /// any pitch can be placed and the rows never move as notes are edited.
    /// </summary>
    public IReadOnlyList<int> Pitches => AllPitches;

    /// <summary>The longest note, in ticks, so the notes held into a span from before it are found.</summary>
    public long LongestNote { get; }

    /// <summary>Whether the part has neither a note nor a syllable to show.</summary>
    public bool IsEmpty => Notes.Count == 0 && Lyrics.Count == 0;

    /// <summary>
    /// The notes that may reach into <paramref name="fromTick"/> to <paramref name="toTick"/>, as indexes into
    /// <see cref="Notes"/> from <c>From</c> up to but not including <c>To</c>: those starting in it, and those starting up
    /// to the longest note before it. Found by binary search.
    /// </summary>
    public (int From, int To) NotesBetween(double fromTick, double toTick) =>
        (FirstNoteFrom(fromTick - LongestNote), FirstNoteFrom(Math.Floor(toTick) + 1));

    /// <summary>The syllables in <paramref name="fromTick"/> to <paramref name="toTick"/>, as <see cref="NotesBetween"/> gives notes.</summary>
    public (int From, int To) LyricsBetween(double fromTick, double toTick) =>
        (FirstLyricFrom(fromTick), FirstLyricFrom(Math.Floor(toTick) + 1));

    /// <summary>The index of the note being sung at <paramref name="tick"/>, or null between notes.</summary>
    public int? NoteAt(double tick)
    {
        var (from, to) = NotesBetween(tick, tick);
        for (int i = to - 1; i >= from; i--)
        {
            if (Notes[i].Tick <= tick && tick < Math.Max(Notes[i].EndTick, Notes[i].Tick + 1))
                return i;
        }

        return null;
    }

    private int FirstNoteFrom(double tick)
    {
        int low = 0, high = Notes.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (Notes[middle].Tick < tick)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private int FirstLyricFrom(double tick)
    {
        int low = 0, high = Lyrics.Count;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (Lyrics[middle].Tick < tick)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
