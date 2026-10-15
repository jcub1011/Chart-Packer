using System.Runtime.CompilerServices;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>A sung note of a vocal track.</summary>
/// <param name="Pitch">The MIDI pitch, <see cref="VocalTrackContent.LowestPitch"/> to <see cref="VocalTrackContent.HighestPitch"/>.</param>
/// <param name="Velocity">The note-on's velocity, kept as it was read; a new note's is the usual one.</param>
public readonly record struct VocalNoteEvent(long Tick, long Length, int Pitch, int Velocity = MidiLayout.NormalVelocity)
{
    /// <summary>The tick the note is let go on.</summary>
    public long EndTick => Tick + Length;

    /// <summary>Where the note is: its tick and pitch.</summary>
    public VocalNotePlace Place => new(Tick, Pitch);
}

/// <summary>Where a vocal note is, which says which note it is: a track has one note at a tick and pitch.</summary>
public readonly record struct VocalNotePlace(long Tick, int Pitch);

/// <summary>A lyric of a vocal track: a syllable, with the symbols the games read after it (<see cref="LyricSyllable"/>).</summary>
/// <param name="Text">The text as the track has it, symbols included.</param>
/// <param name="Type">The meta event it's written as: <see cref="VocalTrackContent.LyricType"/>, or a text event as older charts have them.</param>
public readonly record struct VocalLyricEvent(long Tick, string Text, byte Type = VocalTrackContent.LyricType);

/// <summary>The notes a vocal phrase is written as.</summary>
[Flags]
public enum VocalPhraseMarkers
{
    None = 0,

    /// <summary>Note 105.</summary>
    Primary = 1,

    /// <summary>Note 106, which older charts mark a second singer's phrases with.</summary>
    Secondary = 2,
}

/// <summary>
/// A phrase of a vocal track: a line of lyrics, from <see cref="Tick"/> up to but not including <see cref="EndTick"/>.
/// A 105 and a 106 over the same ticks are one phrase with both <see cref="Markers"/>, so they stay together.
/// </summary>
public readonly record struct VocalPhraseEvent(long Tick, long Length, VocalPhraseMarkers Markers = VocalPhraseMarkers.Primary)
{
    /// <summary>The tick after the last the phrase covers.</summary>
    public long EndTick => Tick + Length;
}

/// <summary>
/// One vocal track (<c>PART VOCALS</c>, <c>HARM1</c> to <c>HARM3</c>) as the editor edits it: its sung notes, lyrics and
/// phrases, and every other event it has kept as it is, in order (charter-spec §4.2, C3 item 7). Immutable: an edit is a
/// new content, written as a new track (<see cref="Write"/>) that replaces the old one.
/// </summary>
/// <remarks>
/// <para>It's read as <see cref="VocalReader"/> reads a track, so <see cref="ToPart"/> is what the reader reads from
/// the track <see cref="Write"/> gives. Notes are kept one to a tick and pitch, each let go no later than the next on its
/// pitch starts, and phrases likewise for each marker, so writing and reading pair the note events up the same way.</para>
/// <para>A content not changed from its <see cref="Source"/> writes the source itself, byte for byte. A changed one is
/// written again: running status isn't kept, the sung notes and phrases are on the first channel, and at a tick the
/// kept events come first, then the lyrics, after the note-offs and before the note-ons.</para>
/// </remarks>
public sealed class VocalTrackContent
{
    /// <summary>The lowest sung pitch the games read.</summary>
    public const int LowestPitch = 36;

    /// <summary>The highest sung pitch the games read.</summary>
    public const int HighestPitch = 84;

    /// <summary>The note a phrase is written as.</summary>
    public const int PhrasePitch = 105;

    /// <summary>The note older charts mark a second singer's phrases with.</summary>
    public const int OtherPhrasePitch = 106;

    /// <summary>A lyric meta event's type.</summary>
    public const byte LyricType = 0x05;

    /// <summary>A text meta event's type, which older charts have their lyrics as.</summary>
    public const byte TextType = 0x01;

    private const byte NameType = 0x03;

    // Within PartEventOrder.Text: the kept events, then the lyrics.
    private const int KeptKey = 0;
    private const int LyricKey = 1;

    // Each track as read, so a track shared by copies of the chart, or by the lint's snapshots, is read once.
    private static readonly ConditionalWeakTable<PassthroughTrack, VocalTrackContent> Read = [];

    private VocalTrackContent(
        string? name,
        PassthroughTrack? source,
        bool isChanged,
        IReadOnlyList<VocalNoteEvent> notes,
        IReadOnlyList<VocalLyricEvent> lyrics,
        IReadOnlyList<VocalPhraseEvent> phrases,
        IReadOnlyList<MidiMessage> kept,
        long endTick)
    {
        Name = name;
        Source = source;
        IsChanged = isChanged;
        Notes = notes;
        Lyrics = lyrics;
        Phrases = phrases;
        Kept = kept;
        EndTick = endTick;
    }

    /// <summary>The track's name, as the loader took it.</summary>
    public string? Name { get; }

    /// <summary>The track this was read from, which an edit of it replaces; null for a part not yet in the chart.</summary>
    public PassthroughTrack? Source { get; }

    /// <summary>Whether the content differs from its <see cref="Source"/>'s, or has none, so it must be written again.</summary>
    public bool IsChanged { get; }

    /// <summary>The sung notes, by tick then pitch.</summary>
    public IReadOnlyList<VocalNoteEvent> Notes { get; }

    /// <summary>The lyrics, by tick, those at a tick in the track's order.</summary>
    public IReadOnlyList<VocalLyricEvent> Lyrics { get; }

    /// <summary>The phrases, by tick.</summary>
    public IReadOnlyList<VocalPhraseEvent> Phrases { get; }

    /// <summary>Every other event, as the track has it, in its order: its name, percussion, overdrive, animations and the rest.</summary>
    internal IReadOnlyList<MidiMessage> Kept { get; }

    /// <summary>The tick the track ends on, which a track written again ends no earlier than.</summary>
    public long EndTick { get; }

    /// <summary>The content of <paramref name="track"/>, read once for each track.</summary>
    /// <exception cref="ArgumentException"><paramref name="track"/> isn't a track.</exception>
    /// <exception cref="ChartFormatException">The track is malformed.</exception>
    public static VocalTrackContent Of(PassthroughTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.IsTrack)
            throw new ArgumentException("Only a track has vocals.", nameof(track));
        return Read.TryGetValue(track, out var known) ? known : Read.GetValue(track, Parse);
    }

    /// <summary>A part with nothing in it, to add to a chart as the track <paramref name="name"/>.</summary>
    public static VocalTrackContent Empty(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(name, null, true, [], [], [], [MidiMessage.Text(0, NameType, name)], 0);
    }

    /// <summary>
    /// The content with <paramref name="notes"/>, <paramref name="lyrics"/> or <paramref name="phrases"/> in place of its
    /// own, each put in order and kept to the rules in the remarks: of two notes at a tick and pitch the longer is kept,
    /// and a note held into the next on its pitch is cut where that starts; phrases likewise for each marker. This one,
    /// when they're all the same as its own.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tick or length is negative, a pitch isn't sung, or a phrase has no marker.</exception>
    public VocalTrackContent With(
        IEnumerable<VocalNoteEvent>? notes = null,
        IEnumerable<VocalLyricEvent>? lyrics = null,
        IEnumerable<VocalPhraseEvent>? phrases = null)
    {
        var newNotes = notes is null ? Notes : NotesInOrder(Checked(notes));
        var newLyrics = lyrics is null ? Lyrics : LyricsInOrder(Checked(lyrics));
        var newPhrases = phrases is null ? Phrases : PhrasesInOrder(Checked(phrases));
        if (newNotes.SequenceEqual(Notes) && newLyrics.SequenceEqual(Lyrics) && newPhrases.SequenceEqual(Phrases))
            return this;
        return new(Name, Source, true, newNotes, newLyrics, newPhrases, Kept, EndTick);
    }

    /// <summary>The part as <see cref="VocalReader.ReadPart"/> reads it from the track <see cref="Write"/> gives.</summary>
    public VocalPart ToPart()
    {
        string name = (Name ?? string.Empty).Trim().ToUpperInvariant();
        var notes = Notes.Select(static n => new VocalNote(n.Tick, n.Length, n.Pitch)).ToList();
        var lyrics = Lyrics
            .Where(static l => l.Text.Trim().Length > 0)
            .Select(static l => new VocalLyric(l.Tick, l.Text.Trim()))
            .ToList();

        // The reader takes the first phrase to end of those at a tick.
        var phrases = Phrases
            .OrderBy(static p => p.Tick)
            .ThenBy(static p => p.EndTick)
            .DistinctBy(static p => p.Tick)
            .Select(static p => new VocalPhrase(p.Tick, p.Length))
            .ToList();
        return new VocalPart(name, notes, lyrics, phrases);
    }

    /// <summary>
    /// The track: the source itself when nothing has changed, otherwise the track written again, with the source's name
    /// and every event kept, ending no earlier than the source did.
    /// </summary>
    /// <exception cref="ChartFormatException">An event is later than a MIDI file can say.</exception>
    public PassthroughTrack Write()
    {
        if (!IsChanged && Source is not null)
            return Source;

        var track = new TrackEvents();
        foreach (var kept in Kept)
            track.Add(kept.Tick, PartEventOrder.Text, KeptKey, kept.Bytes);
        foreach (var lyric in Lyrics)
            track.Add(lyric.Tick, PartEventOrder.Text, LyricKey, MidiMessage.Text(lyric.Tick, lyric.Type, lyric.Text).Bytes);
        foreach (var note in Notes)
            track.Note(note.Tick, note.Length, note.Pitch, note.Velocity);
        foreach (var phrase in Phrases)
        {
            if ((phrase.Markers & VocalPhraseMarkers.Primary) != 0)
                track.Note(phrase.Tick, phrase.Length, PhrasePitch);
            if ((phrase.Markers & VocalPhraseMarkers.Secondary) != 0)
                track.Note(phrase.Tick, phrase.Length, OtherPhrasePitch);
        }

        return PassthroughTrack.Rewritten(track.Chunk(EndTick), Name);
    }

    /// <summary>Whether <paramref name="pitch"/> is one the games read as sung.</summary>
    public static bool IsSung(int pitch) => pitch is >= LowestPitch and <= HighestPitch;

    // Reads the track as VocalReader.ReadTrack does, keeping what it skips.
    private static VocalTrackContent Parse(PassthroughTrack source)
    {
        byte[] bytes = source.Array;
        var notes = new List<VocalNoteEvent>();
        var lyrics = new List<VocalLyricEvent>();
        var phrases = new List<(long Tick, long Length, VocalPhraseMarkers Marker)>();
        var kept = new List<MidiMessage>();
        var open = new Dictionary<int, (long Tick, int Velocity)>();
        long end = 0;

        foreach (var e in MidiFile.ReadEvents(bytes, source.ChunkOf()))
        {
            if (e.IsMeta)
            {
                var (type, _, _) = MidiFile.MetaOf(bytes, e);
                if (type == MidiFile.EndOfTrack)
                    end = Math.Max(end, e.Tick);
                else if (LyricOf(bytes, e) is { } lyric)
                    lyrics.Add(lyric);
                else
                    kept.Add(MidiMessage.Of(bytes, e));
                continue;
            }

            if (e.Kind is not (0x80 or 0x90) || e.DataLength < 2 || !IsModelled(bytes[e.DataStart]))
            {
                kept.Add(MidiMessage.Of(bytes, e));
                continue;
            }

            // A second note-on before the note-off ends the first, as the reader does. A note-off with no note-on is
            // dropped, as the reader skips it: kept, it could end a note written there.
            int pitch = bytes[e.DataStart];
            int velocity = bytes[e.DataStart + 1];
            bool on = e.Kind == 0x90 && velocity > 0;
            if (open.Remove(pitch, out var start))
                Add(pitch, start.Tick, e.Tick, start.Velocity);
            if (on)
                open[pitch] = (e.Tick, velocity);
        }

        foreach (var (pitch, start) in open)
            Add(pitch, start.Tick, start.Tick, start.Velocity);

        void Add(int pitch, long tick, long endTick, int velocity)
        {
            if (pitch == PhrasePitch)
                phrases.Add((tick, Math.Max(1, endTick - tick), VocalPhraseMarkers.Primary));
            else if (pitch == OtherPhrasePitch)
                phrases.Add((tick, Math.Max(1, endTick - tick), VocalPhraseMarkers.Secondary));
            else
                notes.Add(new VocalNoteEvent(tick, Math.Max(0, endTick - tick), pitch, velocity));
        }

        // A 105 and a 106 over the same ticks are one phrase.
        var joined = phrases
            .GroupBy(static p => (p.Tick, p.Length))
            .Select(static g => new VocalPhraseEvent(g.Key.Tick, g.Key.Length, g.Aggregate(VocalPhraseMarkers.None, static (m, p) => m | p.Marker)));

        return new(source.Name, source, false, NotesInOrder(notes), LyricsInOrder(lyrics), PhrasesInOrder(joined), kept, end);
    }

    // A lyric event, or a text event older charts have as one, with text; null for any other.
    private static VocalLyricEvent? LyricOf(byte[] bytes, MidiEvent e)
    {
        if (MidiFile.TextOf(bytes, e, LyricType) is { } lyric)
            return lyric.Trim().Length > 0 ? new VocalLyricEvent(e.Tick, lyric, LyricType) : null;

        // Bracketed text is an animation or a marker.
        if (MidiFile.TextOf(bytes, e, TextType) is { } text && text.Trim().Length > 0 && !text.TrimStart().StartsWith('['))
            return new VocalLyricEvent(e.Tick, text, TextType);
        return null;
    }

    private static bool IsModelled(int pitch) => IsSung(pitch) || pitch is PhrasePitch or OtherPhrasePitch;

    private static List<T> Checked<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        foreach (var item in list)
        {
            switch (item)
            {
                case VocalNoteEvent note:
                    ArgumentOutOfRangeException.ThrowIfNegative(note.Tick, nameof(note.Tick));
                    ArgumentOutOfRangeException.ThrowIfNegative(note.Length, nameof(note.Length));
                    if (!IsSung(note.Pitch))
                        throw new ArgumentOutOfRangeException(nameof(note.Pitch), note.Pitch, "A sung note's pitch is 36 to 84.");
                    if (note.Velocity is < 1 or > 127)
                        throw new ArgumentOutOfRangeException(nameof(note.Velocity), note.Velocity, "A note-on's velocity is 1 to 127.");
                    break;
                case VocalLyricEvent lyric:
                    ArgumentOutOfRangeException.ThrowIfNegative(lyric.Tick, nameof(lyric.Tick));
                    ArgumentNullException.ThrowIfNull(lyric.Text, nameof(lyric.Text));
                    break;
                case VocalPhraseEvent phrase:
                    ArgumentOutOfRangeException.ThrowIfNegative(phrase.Tick, nameof(phrase.Tick));
                    ArgumentOutOfRangeException.ThrowIfLessThan(phrase.Length, 1, nameof(phrase.Length));
                    if ((phrase.Markers & (VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary)) == 0)
                        throw new ArgumentOutOfRangeException(nameof(phrase.Markers), phrase.Markers, "A phrase is written as 105, 106 or both.");
                    break;
            }
        }

        return list;
    }

    // By tick then pitch; of two at a tick and pitch the longer; each cut where the next on its pitch starts.
    private static VocalNoteEvent[] NotesInOrder(IEnumerable<VocalNoteEvent> notes)
    {
        var sorted = notes
            .OrderBy(static n => n.Tick)
            .ThenBy(static n => n.Pitch)
            .ThenByDescending(static n => n.Length)
            .DistinctBy(static n => n.Place)
            .ToArray();
        var next = new Dictionary<int, long>();
        for (int i = sorted.Length - 1; i >= 0; i--)
        {
            var note = sorted[i];
            if (next.TryGetValue(note.Pitch, out long after) && note.EndTick > after)
                sorted[i] = note with { Length = after - note.Tick };
            next[note.Pitch] = note.Tick;
        }

        return sorted;
    }

    private static VocalLyricEvent[] LyricsInOrder(IEnumerable<VocalLyricEvent> lyrics) => [.. lyrics.OrderBy(static l => l.Tick)];

    // By tick, then end; for each marker, a phrase is cut where the next with it starts, and one starting on the same tick
    // as a longer one with it gives it up.
    private static VocalPhraseEvent[] PhrasesInOrder(IEnumerable<VocalPhraseEvent> phrases)
    {
        var sorted = phrases.OrderBy(static p => p.Tick).ThenByDescending(static p => p.Length).ToList();
        var result = new List<VocalPhraseEvent>(sorted.Count);
        var last = new Dictionary<VocalPhraseMarkers, int>();
        foreach (var phrase in sorted)
        {
            var markers = phrase.Markers;
            foreach (var marker in (ReadOnlySpan<VocalPhraseMarkers>)[VocalPhraseMarkers.Primary, VocalPhraseMarkers.Secondary])
            {
                if ((markers & marker) == 0 || !last.TryGetValue(marker, out int at))
                    continue;
                var before = result[at];
                if (before.Tick == phrase.Tick)
                    markers &= ~marker;
                else if (before.EndTick > phrase.Tick)
                    result[at] = before with { Length = phrase.Tick - before.Tick };
            }

            if (markers == VocalPhraseMarkers.None)
                continue;
            foreach (var marker in (ReadOnlySpan<VocalPhraseMarkers>)[VocalPhraseMarkers.Primary, VocalPhraseMarkers.Secondary])
            {
                if ((markers & marker) != 0)
                    last[marker] = result.Count;
            }

            result.Add(phrase with { Markers = markers });
        }

        return [.. result.OrderBy(static p => p.Tick).ThenBy(static p => p.EndTick)];
    }
}
