using ChartShared.Core.Charts;
using ChartShared.Core.Merging;

namespace ChartShared.Core.Library;

/// <summary>Which parts a song's chart has, and which difficulties they have notes for.</summary>
/// <param name="Instruments">The parts with at least one note.</param>
/// <param name="Difficulties">The difficulties with notes on any part.</param>
public sealed record ChartContents(LibraryInstruments Instruments, LibraryDifficulties Difficulties)
{
    /// <summary>A song without a chart.</summary>
    public static ChartContents None { get; } = new(LibraryInstruments.None, LibraryDifficulties.None);

    private static readonly Dictionary<string, LibraryInstruments> PartFlags = new(StringComparer.Ordinal)
    {
        ["guitar"] = LibraryInstruments.Guitar,
        ["guitar_coop"] = LibraryInstruments.GuitarCoop,
        ["rhythm"] = LibraryInstruments.Rhythm,
        ["bass"] = LibraryInstruments.Bass,
        ["drums"] = LibraryInstruments.Drums,
        ["keys"] = LibraryInstruments.Keys,
        ["pro_keys"] = LibraryInstruments.ProKeys,
        ["vocals"] = LibraryInstruments.Vocals,
        ["harmonies"] = LibraryInstruments.Harmonies,
        ["pro_guitar"] = LibraryInstruments.ProGuitar,
        ["pro_guitar_22"] = LibraryInstruments.ProGuitar22,
        ["pro_bass"] = LibraryInstruments.ProBass,
        ["pro_bass_22"] = LibraryInstruments.ProBass22,
        ["guitar_ghl"] = LibraryInstruments.GuitarGhl,
        ["bass_ghl"] = LibraryInstruments.BassGhl,
        ["rhythm_ghl"] = LibraryInstruments.RhythmGhl,
        ["guitar_coop_ghl"] = LibraryInstruments.GuitarCoopGhl,
        ["dance"] = LibraryInstruments.Dance,
    };

    // The parts the note reader reads, by the instruments it reads them as.
    private static readonly Dictionary<ChartInstrument, string> InstrumentParts = MidiPartCatalog.Known
        .SelectMany(p => MidiPartCatalog.InstrumentsOf(p).Select(i => KeyValuePair.Create(i, p.Id)))
        .DistinctBy(p => p.Key)
        .ToDictionary();

    // Pro keys has a track per difficulty.
    private static readonly Dictionary<string, ChartDifficulty> ProKeysDifficulties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PART REAL_KEYS_E"] = ChartDifficulty.Easy,
        ["PART REAL_KEYS_M"] = ChartDifficulty.Medium,
        ["PART REAL_KEYS_H"] = ChartDifficulty.Hard,
        ["PART REAL_KEYS_X"] = ChartDifficulty.Expert,
    };

    // Pro keys' keys are 48 to 72; the pitches around them are markers.
    private const int FirstProKey = 48;
    private const int LastProKey = 72;

    // A vocal line's sung notes are 36 to 84; percussion (96, 97) and phrases (105, 106) aren't counted.
    private const int FirstVocalPitch = 36;
    private const int LastVocalPitch = 84;

    private static readonly Dictionary<string, int> HarmonyLines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HARM1"] = 1,
        ["HARM2"] = 2,
        ["HARM3"] = 3,
    };

    // Pro guitar and bass keep every difficulty on one track: a string per pitch from 24, 48, 72 and 96.
    private static readonly HashSet<string> ProStringParts = new(StringComparer.Ordinal) { "pro_guitar", "pro_guitar_22", "pro_bass", "pro_bass_22" };
    private const int ProStringCount = 6;
    private static readonly (int Base, ChartDifficulty Difficulty)[] ProStringBases =
    [
        (24, ChartDifficulty.Easy),
        (48, ChartDifficulty.Medium),
        (72, ChartDifficulty.Hard),
        (96, ChartDifficulty.Expert),
    ];

    /// <summary>The flag of the known part <paramref name="partId"/>, e.g. <see cref="LibraryInstruments.Vocals"/> for <c>vocals</c>.</summary>
    public static LibraryInstruments FlagOf(string partId)
    {
        ArgumentNullException.ThrowIfNull(partId);
        return PartFlags.GetValueOrDefault(partId);
    }

    /// <summary>
    /// Reads what <paramref name="bytes"/>, a chart stored as <paramref name="storedName"/>, has.
    /// A part counts when it has a note, so the empty tracks a template leaves in a MIDI file don't.
    /// </summary>
    /// <returns>What it has, or <see langword="null"/> when the chart is malformed.</returns>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    public static ChartContents? Read(byte[] bytes, string storedName, ChartReadOptions? options = null)
    {
        // Only whether each difficulty has a note matters here, so counting stops at the first.
        if (TryTally(bytes, storedName, options, noteLimit: 1) is not { } parts)
            return null;

        var instruments = LibraryInstruments.None;
        var difficulties = LibraryDifficulties.None;
        foreach (var (id, part) in parts)
        {
            instruments |= PartFlags[id];
            difficulties |= part.Difficulties;
        }

        return new ChartContents(instruments, difficulties);
    }

    /// <summary>
    /// Reads which known parts <paramref name="bytes"/>, a chart stored as <paramref name="storedName"/>,
    /// has, the difficulties each has notes for, and how many. A part counts when it has a note, as for <see cref="Read"/>.
    /// </summary>
    /// <remarks>
    /// Vocals, harmonies and dance have no difficulties this reads, so they're present with none; vocals'
    /// and each harmony line's notes are still counted. Pro keys' difficulties come from its tracks, and pro
    /// guitar's and bass's from their notes' pitches. A chord counts as one note, and a 2x kick, which only
    /// Expert+ plays, as none.
    /// </remarks>
    /// <returns>The parts by id (<c>guitar</c>, <c>drums</c>, …), or <see langword="null"/> when the chart is malformed.</returns>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    public static IReadOnlyDictionary<string, ChartPart>? ReadParts(byte[] bytes, string storedName, ChartReadOptions? options = null) =>
        TryTally(bytes, storedName, options, int.MaxValue) is { } parts ? ToParts(parts) : null;

    /// <summary>
    /// The ids of the known parts <paramref name="bytes"/>, a chart stored as <paramref name="storedName"/>, has a note
    /// in, as for <see cref="Read"/>; <see langword="null"/> when the chart is malformed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    internal static IReadOnlySet<string>? PartIdsOf(byte[] bytes, string storedName, ChartReadOptions? options = null) =>
        TryTally(bytes, storedName, options, noteLimit: 1)?.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The known parts <paramref name="document"/> has, as for <see cref="ReadParts"/>, with those the note
    /// reader skips read from <paramref name="midi"/>, the MIDI file it was read from (<see langword="null"/> for a <c>.chart</c>).
    /// </summary>
    /// <exception cref="ChartFormatException"><paramref name="midi"/> is malformed.</exception>
    internal static Dictionary<string, ChartPart> PartsOf(ChartDocument document, byte[]? midi) =>
        ToParts(Tally(document, midi, int.MaxValue));

    /// <summary>Tallies <paramref name="bytes"/>' parts as <see cref="Tally"/> does; <see langword="null"/> when the chart is malformed.</summary>
    private static Dictionary<string, PartTally>? TryTally(byte[] bytes, string storedName, ChartReadOptions? options, int noteLimit)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var document = ChartReader.Read(bytes, storedName, options);
            return Tally(document, ChartReader.IsMidi(storedName) ? bytes : null, noteLimit);
        }
        catch (ChartFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The known parts <paramref name="document"/> and <paramref name="midi"/> have, as for <see cref="PartsOf"/>, counting
    /// each difficulty's or line's notes only up to <paramref name="noteLimit"/> on a track.
    /// </summary>
    private static Dictionary<string, PartTally> Tally(ChartDocument document, byte[]? midi, int noteLimit)
    {
        ArgumentNullException.ThrowIfNull(document);
        var parts = new Dictionary<string, PartTally>(StringComparer.Ordinal);
        foreach (var (key, track) in document.Tracks)
        {
            if (InstrumentParts.TryGetValue(key.Instrument, out string? part))
            {
                // A track the reader keeps has notes, though its count can be none, as for only 2x kicks.
                var tally = TallyOf(parts, part);
                tally.Difficulties |= key.Difficulty.ToFlag();
                tally.Add(key.Difficulty, 0, CountOf(track, noteLimit));
            }
        }

        if (midi is not null)
            AddUnreadParts(midi, parts, noteLimit);

        return parts;
    }

    private static Dictionary<string, ChartPart> ToParts(Dictionary<string, PartTally> parts) =>
        parts.ToDictionary(p => p.Key, p => p.Value.ToPart(), StringComparer.Ordinal);

    /// <summary>
    /// The notes on <paramref name="track"/>, up to <paramref name="limit"/>, a chord counting once; 2x kicks, which only
    /// Expert+ plays, aren't counted.
    /// </summary>
    private static int CountOf(NoteTrack track, int limit)
    {
        int count = 0;
        long? last = null;
        foreach (var note in track.Notes)
        {
            if ((note.Flags & NoteFlags.DoubleKick) != 0 || note.Tick == last)
                continue;

            if (++count >= limit)
                break;
            last = note.Tick;
        }

        return count;
    }

    /// <summary>Adds the MIDI file's parts the note reader skips, such as vocals, that have a note.</summary>
    /// <remarks>
    /// The note reader has read every track of <paramref name="bytes"/> by now, so they're well formed. Only each
    /// track's name is read to start with, and only the tracks of parts counted here in full.
    /// </remarks>
    private static void AddUnreadParts(byte[] bytes, Dictionary<string, PartTally> parts, int noteLimit)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(bytes);
        foreach (var chunk in chunks)
        {
            if (chunk.Id != "MTrk"
                || MidiFile.NameOf(bytes, chunk) is not { } name
                || MidiPartCatalog.PartOf(name) is not { IsKnown: true } part
                || MidiPartCatalog.InstrumentsOf(part).Count > 0)
            {
                continue;
            }

            var events = MidiFile.ReadEvents(bytes, chunk);
            if (!HasNote(bytes, events))
                continue;

            var tally = TallyOf(parts, part.Id);
            if (ProStringParts.Contains(part.Id))
            {
                foreach (var (first, difficulty) in ProStringBases)
                    tally.Add(difficulty, 0, CountOf(bytes, events, first, first + ProStringCount - 1, noteLimit));
            }
            else if (ProKeysDifficulties.TryGetValue(name.Trim(), out var difficulty))
            {
                // Any note on the track, not just a key, marks its difficulty as charted.
                tally.Difficulties |= difficulty.ToFlag();
                tally.Add(difficulty, 0, CountOf(bytes, events, FirstProKey, LastProKey, noteLimit));
            }
            else if (part.Id == "vocals")
            {
                tally.Add(null, 0, CountOf(bytes, events, FirstVocalPitch, LastVocalPitch, noteLimit));
            }
            else if (HarmonyLines.TryGetValue(name.Trim(), out int line))
            {
                tally.Add(null, line, CountOf(bytes, events, FirstVocalPitch, LastVocalPitch, noteLimit));
            }
        }
    }

    /// <summary>
    /// How many ticks of <paramref name="events"/>, a track in tick order, start a note pitched <paramref name="low"/> to
    /// <paramref name="high"/>, up to <paramref name="limit"/>; a chord counting once.
    /// </summary>
    private static int CountOf(byte[] bytes, List<MidiEvent> events, int low, int high, int limit)
    {
        int count = 0;
        long? last = null;
        foreach (var e in events)
        {
            if (e.Kind != 0x90 || e.DataLength < 2 || bytes[e.DataStart + 1] == 0 || e.Tick == last)
                continue;

            int pitch = bytes[e.DataStart];
            if (pitch >= low && pitch <= high)
            {
                if (++count >= limit)
                    break;
                last = e.Tick;
            }
        }

        return count;
    }

    private static PartTally TallyOf(Dictionary<string, PartTally> parts, string id)
    {
        if (!parts.TryGetValue(id, out var tally))
            parts[id] = tally = new PartTally();
        return tally;
    }

    private static bool HasNote(byte[] bytes, List<MidiEvent> events)
    {
        foreach (var e in events)
        {
            if (e.Kind == 0x90 && e.DataLength >= 2 && bytes[e.DataStart + 1] > 0)
                return true;
        }

        return false;
    }

    /// <summary>A part's difficulties and note counts while the chart is read.</summary>
    private sealed class PartTally
    {
        private readonly Dictionary<(ChartDifficulty? Difficulty, int Line), int> _counts = [];

        public LibraryDifficulties Difficulties { get; set; }

        /// <summary>Adds <paramref name="count"/> notes on <paramref name="difficulty"/> or <paramref name="line"/>; a difficulty with any has notes.</summary>
        public void Add(ChartDifficulty? difficulty, int line, int count)
        {
            if (count <= 0)
                return;

            _counts[(difficulty, line)] = _counts.GetValueOrDefault((difficulty, line)) + count;
            if (difficulty is { } d)
                Difficulties |= d.ToFlag();
        }

        public ChartPart ToPart() => new(
            Difficulties,
            [.. _counts.Select(c => new NoteCount(c.Key.Difficulty, c.Key.Line, c.Value)).OrderBy(c => c.Difficulty).ThenBy(c => c.Line)]);
    }
}
