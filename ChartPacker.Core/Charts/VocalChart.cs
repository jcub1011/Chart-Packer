using ChartPacker.Core.Scanning;
using ChartPacker.Core.Text;

namespace ChartPacker.Core.Charts;

/// <summary>A sung note of a vocal track.</summary>
/// <param name="Pitch">The MIDI pitch, 36 to 84.</param>
public readonly record struct VocalNote(long Tick, long Length, int Pitch);

/// <summary>A lyric event: one syllable, as the chart spells it, symbols included; see <see cref="LyricSyllable"/>.</summary>
public readonly record struct VocalLyric(long Tick, string Text);

/// <summary>A phrase of a vocal track: a line of lyrics, shown and scored together.</summary>
public readonly record struct VocalPhrase(long Tick, long Length);

/// <summary>One vocal track, <c>PART VOCALS</c> or a harmony: its notes, lyrics and phrases, each in tick order.</summary>
public sealed record VocalPart(string TrackName, IReadOnlyList<VocalNote> Notes, IReadOnlyList<VocalLyric> Lyrics, IReadOnlyList<VocalPhrase> Phrases);

/// <summary>A chart's vocal tracks, and the tempo map that places them.</summary>
public sealed record VocalChart(TempoMap Tempo, IReadOnlyList<VocalPart> Parts)
{
    /// <summary>
    /// The part the lyrics are shown from: the first of <c>PART VOCALS</c> and the harmonies, in that
    /// order, with lyrics; otherwise the first with notes. An empty <c>PART VOCALS</c>, as a template
    /// leaves, gives way to a harmony that has them.
    /// </summary>
    public VocalPart? Main => Parts.FirstOrDefault(p => p.Lyrics.Count > 0) ?? Parts.FirstOrDefault(p => p.Notes.Count > 0) ?? Parts.FirstOrDefault();

    /// <summary>Whether any vocal track has a lyric or a note.</summary>
    public bool HasVocals => Parts.Any(p => p.Lyrics.Count > 0 || p.Notes.Count > 0);

    /// <summary>The tick of the first note, lyric or phrase of any vocal track; <see langword="null"/> when there's none.</summary>
    public long? FirstTick => Parts
        .SelectMany(p => p.Notes.Select(n => n.Tick).Concat(p.Lyrics.Select(l => l.Tick)).Concat(p.Phrases.Select(f => f.Tick)))
        .Select(t => (long?)t)
        .Min();
}

/// <summary>A lyric as it's shown, with the chart's symbols (Rock Band and Clone Hero conventions) worked out.</summary>
/// <param name="Text">What's shown; empty for a slide.</param>
/// <param name="JoinsNext">The next syllable follows on without a space: the lyric ended in <c>-</c> or <c>=</c>.</param>
/// <param name="IsUnpitched">The note is spoken or shouted rather than sung: <c>#</c> or <c>^</c>.</param>
/// <param name="IsSlide">The lyric is <c>+</c>: the note carries the previous syllable on to a new pitch.</param>
public readonly record struct LyricSyllable(string Text, bool JoinsNext, bool IsUnpitched, bool IsSlide)
{
    /// <summary>Works out the syllable <paramref name="lyric"/> stands for.</summary>
    /// <remarks>
    /// Rich-text tags, such as <c>&lt;b&gt;</c> or <c>&lt;color=#FF0000&gt;</c>, are removed; see
    /// <see cref="RichText"/>. A tag that opens in one syllable and closes in a later one is removed
    /// from both. A trailing <c>-</c> joins the next syllable; <c>=</c> does too, and shows as a
    /// hyphen. <c>#</c> and <c>^</c> mark an unpitched note; <c>*</c>, <c>%</c>, <c>$</c> and
    /// <c>/</c> are markings a player doesn't see. <c>_</c> shows as a space and <c>§</c> as a tie
    /// between two syllables sung on one note.
    /// </remarks>
    public static LyricSyllable Of(string lyric)
    {
        ArgumentNullException.ThrowIfNull(lyric);
        string text = RichText.ToPlainText(lyric).Replace('\n', ' ').Trim();
        if (text == "+")
            return new LyricSyllable(string.Empty, false, false, true);

        bool joins = false, unpitched = false;
        string suffix = string.Empty;
        while (text.Length > 0)
        {
            char last = text[^1];
            if (last == '-')
                joins = true;
            else if (last == '=')
                (joins, suffix) = (true, "-");
            else if (last is '#' or '^')
                unpitched = true;
            else if (last is not ('*' or '%' or '$' or '/'))
                break;
            text = text[..^1];
        }

        text = text.TrimStart('$').Replace('_', ' ').Replace('§', '‿') + suffix;
        return new LyricSyllable(text, joins, unpitched, false);
    }
}

/// <summary>Reads the vocal tracks of a chart: <c>PART VOCALS</c> and <c>HARM1</c> to <c>HARM3</c>.</summary>
/// <remarks>
/// <para>In a MIDI file each vocal track's notes 36 to 84 are the sung notes, 105 and 106 its
/// phrases, and its lyric events (or text events that aren't <c>[bracketed]</c>, as older charts
/// have them) its lyrics. Percussion notes and anything else are skipped.</para>
/// <para>A <c>.chart</c> has lyrics only: <c>lyric</c> events between <c>phrase_start</c> and
/// <c>phrase_end</c> in <c>[Events]</c>, read as one <c>PART VOCALS</c> without notes.</para>
/// </remarks>
public static class VocalReader
{
    /// <summary>The name of the main vocal track.</summary>
    public const string VocalsTrackName = "PART VOCALS";

    private const int LowestPitch = 36;
    private const int HighestPitch = 84;
    private const int PhrasePitch = 105;
    private const int OtherPhrasePitch = 106;

    /// <summary>The vocal tracks, in the order they're listed.</summary>
    public static IReadOnlyList<string> TrackNames { get; } = [VocalsTrackName, "HARM1", "HARM2", "HARM3"];

    /// <summary>Whether <paramref name="trackName"/> is a vocal track, ignoring case and surrounding space.</summary>
    public static bool IsVocalTrack(string? trackName) =>
        trackName is not null && TrackNames.Contains(trackName.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the vocal tracks of the chart <paramref name="bytes"/>, stored as <paramref name="storedName"/>.</summary>
    /// <param name="tempo">
    /// The chart's tempo map, when it's been read already: a <c>.chart</c>'s is otherwise read with the
    /// whole chart. A MIDI file's is read from its tracks either way.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    /// <exception cref="ChartFormatException">The chart is malformed.</exception>
    public static VocalChart Read(byte[] bytes, string storedName, TempoMap? tempo = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(storedName);
        return ChartReader.IsMidi(storedName) ? ReadMidi(bytes) : ReadChart(bytes, tempo);
    }

    /// <summary>
    /// Reads one vocal track the editor keeps as it was loaded (charter-spec §4.2), as <see cref="Read"/> reads it from
    /// the whole file. Its ticks are the chart's; the editor's tempo map places them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="track"/> isn't a track.</exception>
    /// <exception cref="ChartFormatException">The track is malformed.</exception>
    public static VocalPart ReadPart(Charting.PassthroughTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!track.IsTrack)
            throw new ArgumentException("Only a track has vocals.", nameof(track));
        string name = (track.Name ?? string.Empty).Trim().ToUpperInvariant();
        return ReadTrack(track.Array, name, MidiFile.ReadEvents(track.Array, track.ChunkOf()));
    }

    /// <summary>
    /// The lyrics and phrases of a <c>.chart</c>'s <c>[Events]</c> <paramref name="lines"/>; <see langword="null"/>
    /// when there's no lyric. A <c>phrase_start</c> ends any phrase still open, and a phrase left open
    /// ends a beat after the last vocal event. Every phrase lasts at least a tick.
    /// </summary>
    internal static (List<VocalLyric> Lyrics, List<VocalPhrase> Phrases)? ChartVocals(IEnumerable<string> lines, int resolution)
    {
        var events = lines.Select(TextEvent).OfType<(long Tick, string Text)>().Where(e => IsChartVocalEvent(e.Text)).OrderBy(e => e.Tick).ToList();
        if (!events.Any(e => IsLyricEvent(e.Text)))
            return null;

        var lyrics = new List<VocalLyric>();
        var phrases = new List<VocalPhrase>();
        long? phraseStart = null;
        long lastTick = 0;
        void EndPhrase(long tick)
        {
            if (phraseStart is { } start)
                phrases.Add(new VocalPhrase(start, Math.Max(1, tick - start)));
            phraseStart = null;
        }

        foreach (var (tick, text) in events)
        {
            if (text.Equals("phrase_start", StringComparison.OrdinalIgnoreCase))
            {
                EndPhrase(tick);
                phraseStart = tick;
            }
            else if (text.Equals("phrase_end", StringComparison.OrdinalIgnoreCase))
            {
                EndPhrase(tick);
            }
            else
            {
                lyrics.Add(new VocalLyric(tick, text["lyric ".Length..].Trim()));
            }

            lastTick = tick;
        }

        EndPhrase(lastTick + resolution);
        return (lyrics, phrases);
    }

    /// <summary>Whether the <c>.chart</c> global event <paramref name="text"/> is a lyric or a phrase marker.</summary>
    internal static bool IsChartVocalEvent(string text) =>
        IsLyricEvent(text)
        || text.Equals("phrase_start", StringComparison.OrdinalIgnoreCase)
        || text.Equals("phrase_end", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>.chart</c> line <c>tick = E text</c>, with the quotes around the text removed.</summary>
    internal static (long Tick, string Text)? TextEvent(string line)
    {
        int equals = line.IndexOf('=');
        if (equals < 0 || !long.TryParse(line.AsSpan(0, equals).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long tick))
            return null;

        string rest = line[(equals + 1)..].Trim();
        if (rest.Length < 2 || char.ToUpperInvariant(rest[0]) != 'E' || !char.IsWhiteSpace(rest[1]))
            return null;

        string text = rest[1..].Trim().Trim('"').Trim();
        return text.Length == 0 ? null : (tick, text);
    }

    private static bool IsLyricEvent(string text) => text.StartsWith("lyric ", StringComparison.OrdinalIgnoreCase);

    private static VocalChart ReadChart(byte[] bytes, TempoMap? tempo)
    {
        tempo ??= ChartReader.Read(bytes, ChartFiles.ChartName).Tempo;
        var lines = ChartTextReader.Sections(ChartTextReader.Decode(bytes))
            .Where(s => s.Name == "Events")
            .SelectMany(s => s.Lines);

        return ChartVocals(lines, tempo.Resolution) is var (lyrics, phrases)
            ? new VocalChart(tempo, [new VocalPart(VocalsTrackName, [], lyrics, phrases)])
            : new VocalChart(tempo, []);
    }

    private static VocalChart ReadMidi(byte[] bytes)
    {
        var (_, division, chunks) = MidiFile.ReadChunks(bytes);
        var tracks = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(bytes, c)).ToList();
        var tempo = MidiChartReader.TempoOf(bytes, division, tracks);

        // The first track of each name, as for the instruments.
        var parts = new List<VocalPart>();
        foreach (var events in tracks)
        {
            if (MidiFile.NameOf(bytes, events) is { } name && IsVocalTrack(name) && !parts.Any(p => p.TrackName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                parts.Add(ReadTrack(bytes, name.ToUpperInvariant(), events));
        }

        return new VocalChart(tempo, [.. parts.OrderBy(p => TrackNames.ToList().FindIndex(n => n.Equals(p.TrackName, StringComparison.OrdinalIgnoreCase)))]);
    }

    private static VocalPart ReadTrack(byte[] bytes, string name, List<MidiEvent> events)
    {
        var notes = new List<VocalNote>();
        var lyrics = new List<VocalLyric>();
        var phrases = new List<VocalPhrase>();
        var open = new Dictionary<int, long>();

        foreach (var e in events)
        {
            if (e.IsMeta)
            {
                if ((MidiFile.TextOf(bytes, e, 0x05) ?? LyricText(MidiFile.TextOf(bytes, e, 0x01))) is { } text && text.Trim().Length > 0)
                    lyrics.Add(new VocalLyric(e.Tick, text.Trim()));
                continue;
            }

            if (e.Kind is not (0x80 or 0x90) || e.DataLength < 2)
                continue;

            int pitch = bytes[e.DataStart];
            bool on = e.Kind == 0x90 && bytes[e.DataStart + 1] > 0;
            if (!IsKept(pitch))
                continue;

            // A second note-on before the note-off ends the first, as the instrument reader does.
            if (open.Remove(pitch, out long start))
                Add(pitch, start, e.Tick);
            if (on)
                open[pitch] = e.Tick;
        }

        foreach (var (pitch, start) in open)
            Add(pitch, start, start);

        void Add(int pitch, long start, long end)
        {
            if (pitch is PhrasePitch or OtherPhrasePitch)
                phrases.Add(new VocalPhrase(start, Math.Max(1, end - start)));
            else
                notes.Add(new VocalNote(start, Math.Max(0, end - start), pitch));
        }

        return new VocalPart(
            name,
            [.. notes.OrderBy(n => n.Tick).ThenBy(n => n.Pitch)],
            [.. lyrics.OrderBy(l => l.Tick)],
            [.. phrases.OrderBy(p => p.Tick).DistinctBy(p => p.Tick)]);
    }

    private static bool IsKept(int pitch) => pitch is >= LowestPitch and <= HighestPitch or PhrasePitch or OtherPhrasePitch;

    // Older charts have their lyrics as text events; bracketed text is an animation or a marker.
    private static string? LyricText(string? text) => text is null || text.TrimStart().StartsWith('[') ? null : text;
}
