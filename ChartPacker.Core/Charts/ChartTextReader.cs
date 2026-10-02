using System.Text;

namespace ChartPacker.Core.Charts;

/// <summary>
/// Reads a <c>.chart</c> as YARG does (<c>ChartReader</c> in YARG.Core): the <c>[Song]</c> resolution and
/// offset, the <c>[SyncTrack]</c> tempo map, and the note sections of the instruments in
/// <see cref="ChartInstrument"/>. Anything else is skipped.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The first section must be <c>[Song]</c> and the second <c>[SyncTrack]</c>. A section starts with a
/// <c>[name]</c> at the start of a line, and its body ends at a <c>}</c> at the start of a line.</item>
/// <item>A note section is named for a difficulty and ends with an instrument's name, both matched exactly.</item>
/// <item>A track line's tick must be digits. Its lines are taken in tick order, those at a tick in file order,
/// and only the first letter of the type is read. Numbers are read digit by digit, as YARG reads them.</item>
/// </list>
/// </remarks>
internal static class ChartTextReader
{
    private const int DefaultResolution = 192;

    private static readonly string[] Difficulties = ["Easy", "Medium", "Hard", "Expert"];

    /// <summary>The instrument part of each note section's name, e.g. <c>ExpertDoubleBass</c>, in the order YARG tries them.</summary>
    internal static IReadOnlyList<(string Name, ChartInstrument Instrument)> SectionInstruments { get; } =
    [
        ("Single", ChartInstrument.Guitar),
        ("DoubleGuitar", ChartInstrument.GuitarCoop),
        ("DoubleBass", ChartInstrument.Bass),
        ("DoubleRhythm", ChartInstrument.Rhythm),
        ("Drums", ChartInstrument.Drums),
        ("Keyboard", ChartInstrument.Keys),
        ("GHLGuitar", ChartInstrument.GhlGuitar),
        ("GHLBass", ChartInstrument.GhlBass),
        ("GHLRhythm", ChartInstrument.GhlRhythm),
        ("GHLCoop", ChartInstrument.GhlCoop),
    ];

    /// <summary>The name of the note section for <paramref name="key"/>, e.g. <c>ExpertSingle</c>.</summary>
    internal static string SectionName(ChartTrackKey key) =>
        key.Difficulty + SectionInstruments.First(p => p.Instrument == key.Instrument).Name;

    /// <summary>The track a note section's name is for, if it's one that's read: it starts with a difficulty and ends with an instrument.</summary>
    internal static ChartTrackKey? TrackOf(string sectionName)
    {
        for (int d = 0; d < Difficulties.Length; d++)
        {
            if (!sectionName.StartsWith(Difficulties[d], StringComparison.Ordinal))
                continue;
            foreach (var (name, instrument) in SectionInstruments)
            {
                if (sectionName.EndsWith(name, StringComparison.Ordinal))
                    return new ChartTrackKey(instrument, (ChartDifficulty)d);
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// How long a note of <paramref name="length"/> ticks is held, as <paramref name="options"/> read it: a
    /// <c>.chart</c> has no sustain cutoff unless song.ini sets one, and then one held for less isn't a sustain.
    /// </summary>
    internal static long SustainOf(long length, ChartReadOptions options) =>
        length <= 0 || (options.SustainCutoff is { } cutoff && length < cutoff) ? 0 : length;

    /// <exception cref="ChartFormatException">The file isn't a <c>.chart</c> YARG can read.</exception>
    public static ChartDocument Read(byte[] bytes, ChartReadOptions? options = null)
    {
        options ??= ChartReadOptions.Default;
        var sections = Sections(Decode(bytes));
        if (sections.Count < 1 || sections[0].Name != "Song")
            throw new ChartFormatException("The chart doesn't start with a [Song] section.");
        if (sections.Count < 2 || sections[1].Name != "SyncTrack")
            throw new ChartFormatException("The chart's [Song] section isn't followed by a [SyncTrack] section.");

        int resolution = DefaultResolution;
        TimeSpan? offset = null;
        bool hasResolution = false;
        foreach (string line in sections[0].Lines)
        {
            string key = SplitOnce(line, '=', out string value);
            if (key == "Resolution" && !hasResolution)
            {
                resolution = FastInt(value.Trim('"'));
                hasResolution = true;
            }
            else if (key == "Offset" && SongIniValues.Real(value) is { } seconds && double.IsFinite(seconds) && Math.Abs(seconds) < 86_400)
            {
                offset = TimeSpan.FromSeconds(seconds);
            }
        }

        if (resolution < 1)
            throw new ChartFormatException("The chart's resolution isn't a positive number.");

        var tempo = new TempoMap(resolution, SyncTrack(sections[1].Lines, out var signatures), signatures);
        var trackLines = new Dictionary<ChartTrackKey, List<string>>();
        foreach (var (name, lines) in sections.Skip(2))
        {
            if (TrackOf(name) is not { } key)
                continue;
            if (!trackLines.TryGetValue(key, out var all))
                trackLines[key] = all = [];
            all.AddRange(lines);
        }

        var tracks = new Dictionary<ChartTrackKey, NoteTrack>();
        long hopoThreshold = options.HopoThreshold(resolution, chart: true);
        foreach (var (key, lines) in trackLines)
        {
            if (ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums)
                continue;
            var events = TrackEvents(lines);
            var notes = Frets(key, events, hopoThreshold, options, StarPowerOf(events));
            if (notes.Count > 0)
                tracks[key] = new NoteTrack(notes, StarPowerOf(events));
        }

        var layout = DrumsLayoutOf(sections, options);
        var drums = Drums(trackLines.Where(p => p.Key.Instrument == ChartInstrument.Drums).ToDictionary(p => p.Key.Difficulty, p => TrackEvents(p.Value)), layout);
        foreach (var (difficulty, track) in drums)
            tracks[new ChartTrackKey(ChartInstrument.Drums, difficulty)] = track;

        return new ChartDocument(tempo, tracks, offset, layout == DrumsLayout.FiveLane) { IsProDrums = layout == DrumsLayout.Pro };
    }

    /// <summary>
    /// The drums' layout, from song.ini and the drum sections' notes in file order: a section for a difficulty
    /// already seen isn't looked at (see <see cref="DrumsLayouts"/>).
    /// </summary>
    internal static DrumsLayout DrumsLayoutOf(List<(string Name, List<string> Lines)> sections, ChartReadOptions options)
    {
        var layouts = new DrumsLayouts(options, midi: false);
        var seen = new HashSet<ChartDifficulty>();
        foreach (var (name, lines) in sections.Skip(2))
        {
            if (TrackOf(name) is not { Instrument: ChartInstrument.Drums } key || !seen.Add(key.Difficulty))
                continue;
            foreach (string line in lines)
            {
                if (Event(line) is not { Type: 'N' } e)
                    continue;
                if (e.Number == 5)
                    layouts.FifthPad();
                else if (e.Number is >= 66 and <= 68)
                    layouts.ProMarker();
            }
        }

        return layouts.Layout;
    }

    private static List<ChartNote> Frets(ChartTrackKey key, List<TrackEvent> events, long hopoThreshold, ChartReadOptions options, IReadOnlyList<StarPowerPhrase> starPower)
    {
        bool sixFret = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.SixFret;
        var gems = new List<Gem>();
        var forced = new HashSet<long>();
        var taps = new HashSet<long>();
        foreach (var e in events.Where(e => e.Type == 'N'))
        {
            // White 1-3, then black 1-2; black 3 comes last. Five-fret: green to orange.
            int? lane = e.Number switch
            {
                >= 0 and <= 4 => e.Number,
                8 when sixFret => 5,
                7 => ChartNote.OpenLane,
                _ => null,
            };
            if (lane is { } l)
                gems.Add(new Gem(e.Tick, SustainOf(e.Length, options), l));
            else if (e.Number == 5)
                forced.Add(e.Tick);
            else if (e.Number == 6)
                taps.Add(e.Tick);
        }

        return NoteBuilder.Frets(gems, hopoThreshold, tick => new FretMarks(forced.Contains(tick), false, false, taps.Contains(tick)), starPower, midiChordRule: false);
    }

    /// <summary>Each difficulty's drum track, from its <paramref name="events"/>.</summary>
    /// <param name="cymbal">Whether a difficulty's pad at a tick is a cymbal, in place of its cymbal markers, where it says.</param>
    internal static Dictionary<ChartDifficulty, NoteTrack> Drums(Dictionary<ChartDifficulty, List<TrackEvent>> events, DrumsLayout layout, Func<ChartDifficulty, long, int, bool?>? cymbal = null)
    {
        var gems = new Dictionary<ChartDifficulty, List<DrumGem>>();
        var mixes = new Dictionary<ChartDifficulty, List<(long, bool)>>();
        var starPower = new Dictionary<ChartDifficulty, IReadOnlyList<StarPowerPhrase>>();
        foreach (var (difficulty, list) in events)
        {
            // Cymbal markers 66-68 mark the yellow, blue and fourth pads at their tick.
            var cymbals = list.Where(e => e.Type == 'N' && e.Number is >= 66 and <= 68).Select(e => (e.Tick, Pad: e.Number - 64)).ToHashSet();
            gems[difficulty] =
            [
                .. list
                    .Where(e => e.Type == 'N' && e.Number is (>= 0 and <= 5) or 32)
                    .Select(e => e.Number == 32
                        ? new DrumGem(e.Tick, 0, false, DoubleKick: true)
                        : new DrumGem(e.Tick, e.Number, cymbal?.Invoke(difficulty, e.Tick, e.Number) ?? cymbals.Contains((e.Tick, e.Number)), false)),
            ];
            mixes[difficulty] =
            [
                .. list
                    .Where(e => e.Type == 'E')
                    .Select(e => ChartTextEvents.TryParseMix(e.Text, out var d, out bool flip) && d == difficulty ? (e.Tick, (bool?)flip) : (e.Tick, null))
                    .Where(m => m.Item2 is not null)
                    .Select(m => (m.Tick, m.Item2!.Value)),
            ];
            starPower[difficulty] = StarPowerOf(list);
        }

        var tracks = new Dictionary<ChartDifficulty, NoteTrack>();
        foreach (var (difficulty, notes) in DrumNotes.Build(gems, mixes, starPower, layout))
        {
            if (notes.Count > 0)
                tracks[difficulty] = new NoteTrack(notes, starPower[difficulty]);
        }

        return tracks;
    }

    private static List<StarPowerPhrase> StarPowerOf(List<TrackEvent> events) =>
        [.. events.Where(e => e.Type == 'S' && e.Number == 2).Select(e => new StarPowerPhrase(e.Tick, e.Length))];

    /// <summary>The tempo changes and time signatures; a line whose tick is before the one above it is skipped.</summary>
    private static List<TempoChange> SyncTrack(List<string> lines, out List<TimeSignature> signatures)
    {
        var tempos = new List<TempoChange>();
        signatures = [];
        long previous = 0;
        foreach (string line in lines)
        {
            string tickText = SplitOnce(line, '=', out string rest);
            long tick = (uint)FastInt(tickText);
            if (tick < previous)
                continue;
            previous = tick;

            string type = NextWord(rest, out rest);
            if (type == "B")
            {
                // A tempo of 0 can't be played; it's left out.
                long bpm1000 = (uint)FastInt(NextWord(rest, out _));
                if (bpm1000 > 0)
                    tempos.Add(new TempoChange(tick, 60_000_000_000L / bpm1000));
            }
            else if (type == "TS")
            {
                int numerator = FastInt(NextWord(rest, out rest));
                string power = NextWord(rest, out _);
                int denominatorPower = power.Length == 0 ? 2 : FastInt(power);
                if (numerator > 0 && denominatorPower is >= 0 and < 7)
                    signatures.Add(new TimeSignature(tick, numerator, 1 << denominatorPower));
            }
        }

        return tempos;
    }

    /// <summary>A line <c>tick = T number length</c> or <c>tick = E text</c> of a note section.</summary>
    /// <param name="Type">The type's first letter: <c>N</c> a note or flag, <c>S</c> a phrase, <c>E</c> a text event.</param>
    /// <param name="Length">A note's or phrase's length, read as YARG's unsigned number, so a negative one is huge.</param>
    /// <param name="Text">A text event's text, normalized (see <see cref="ChartTextEvents.Normalize"/>).</param>
    internal readonly record struct TrackEvent(long Tick, char Type, int Number, long Length, string Text);

    /// <summary>A note section's lines as events, in tick order, those at a tick in the order given.</summary>
    internal static List<TrackEvent> TrackEvents(IEnumerable<string> lines) =>
        [.. lines.Select(Event).OfType<TrackEvent>().Select((e, i) => (e, i)).OrderBy(p => p.e.Tick).ThenBy(p => p.i).Select(p => p.e)];

    /// <summary>One note-section line as an event; <see langword="null"/> when its tick isn't digits or it has no type.</summary>
    internal static TrackEvent? Event(string line)
    {
        string tickText = SplitOnce(line, '=', out string rest);
        if (tickText.Length == 0 || !tickText.All(char.IsAsciiDigit))
            return null;
        long tick = 0;
        foreach (char c in tickText)
            tick = unchecked((uint)((tick * 10) + (c - '0')));

        string type = NextWord(rest, out rest);
        if (type.Length == 0)
            return null;
        if (type[0] == 'E')
            return new TrackEvent(tick, 'E', 0, 0, ChartTextEvents.Normalize(TrimOnce(rest, '"')));

        int number = FastInt(NextWord(rest, out rest));
        long length = (uint)FastInt(NextWord(rest, out _));
        return new TrackEvent(tick, type[0], number, length, string.Empty);
    }

    /// <summary>A number read as YARG's <c>FastInt32Parse</c> reads it: each character's distance from <c>0</c>, as a digit, wrapping.</summary>
    internal static int FastInt(string text)
    {
        int value = 0;
        foreach (char c in text)
            value = unchecked((value * 10) + (c - '0'));
        return value;
    }

    private static string SplitOnce(string text, char separator, out string rest)
    {
        int at = text.IndexOf(separator);
        if (at < 0)
        {
            rest = string.Empty;
            return text.Trim();
        }

        rest = text[(at + 1)..].Trim();
        return text[..at].Trim();
    }

    private static string NextWord(string text, out string rest) => SplitOnce(text, ' ', out rest);

    private static string TrimOnce(string text, char c)
    {
        if (text.Length > 0 && text[0] == c)
            text = text[1..];
        if (text.Length > 0 && text[^1] == c)
            text = text[..^1];
        return text;
    }

    /// <summary>
    /// The <c>[name]</c> sections and the trimmed lines of their bodies, as YARG finds them: a name's <c>[</c> starts
    /// a line, its body opens at the next <c>{</c>, and closes at a <c>}</c> that starts a line.
    /// </summary>
    /// <exception cref="ChartFormatException">A section's name or body isn't closed, or it has no body.</exception>
    internal static List<(string Name, List<string> Lines)> Sections(string text)
    {
        var sections = new List<(string, List<string>)>();
        int index = 0;
        while (true)
        {
            int open = NextAtLineStart(text, index, '[');
            if (open < 0)
                return sections;

            int close = text.IndexOf(']', open + 1);
            if (close < 0)
                throw new ChartFormatException("A section name isn't closed with ].");
            string name = text[(open + 1)..close];
            if (name.Contains('\n') || name.Contains('\r'))
                throw new ChartFormatException($"The section name [{name.Trim()}] spans lines.");

            int body = text.IndexOf('{', close + 1);
            if (body < 0)
                throw new ChartFormatException($"The section [{name}] has no body.");
            int end = NextAtLineStart(text, body + 1, '}');
            if (end < 0)
                throw new ChartFormatException($"The section [{name}] isn't closed with }}.");

            sections.Add((name, [.. text[(body + 1)..end].Split('\n').Select(l => l.Trim())]));
            index = end + 1;
        }
    }

    /// <summary>The next <paramref name="c"/> from <paramref name="start"/> with only spaces or control characters before it on its line.</summary>
    private static int NextAtLineStart(string text, int start, char c)
    {
        for (int at = text.IndexOf(c, start); at >= 0; at = text.IndexOf(c, at + 1))
        {
            int before = at - 1;
            while (before >= start && text[before] <= ' ' && text[before] != '\n')
                before--;
            if (before < start || text[before] == '\n')
                return at;
        }

        return -1;
    }

    internal static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
