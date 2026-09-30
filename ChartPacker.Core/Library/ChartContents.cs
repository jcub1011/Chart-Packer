using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;

namespace ChartPacker.Core.Library;

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
    private static readonly Dictionary<string, LibraryDifficulties> ProKeysDifficulties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PART REAL_KEYS_E"] = LibraryDifficulties.Easy,
        ["PART REAL_KEYS_M"] = LibraryDifficulties.Medium,
        ["PART REAL_KEYS_H"] = LibraryDifficulties.Hard,
        ["PART REAL_KEYS_X"] = LibraryDifficulties.Expert,
    };

    // Pro guitar and bass keep every difficulty on one track: a string per pitch from 24, 48, 72 and 96.
    private static readonly HashSet<string> ProStringParts = new(StringComparer.Ordinal) { "pro_guitar", "pro_guitar_22", "pro_bass", "pro_bass_22" };
    private const int ProStringCount = 6;
    private static readonly (int Base, LibraryDifficulties Difficulty)[] ProStringBases =
    [
        (24, LibraryDifficulties.Easy),
        (48, LibraryDifficulties.Medium),
        (72, LibraryDifficulties.Hard),
        (96, LibraryDifficulties.Expert),
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
        if (ReadParts(bytes, storedName, options) is not { } parts)
            return null;

        var instruments = LibraryInstruments.None;
        var difficulties = LibraryDifficulties.None;
        foreach (var (part, partDifficulties) in parts)
        {
            instruments |= PartFlags[part];
            difficulties |= partDifficulties;
        }

        return new ChartContents(instruments, difficulties);
    }

    /// <summary>
    /// Reads which known parts <paramref name="bytes"/>, a chart stored as <paramref name="storedName"/>,
    /// has, and the difficulties each has notes for. A part counts when it has a note, as for <see cref="Read"/>.
    /// </summary>
    /// <remarks>
    /// Vocals, harmonies and dance have no difficulties this reads, so they're present with none.
    /// Pro keys' difficulties come from its tracks, and pro guitar's and bass's from their notes' pitches.
    /// </remarks>
    /// <returns>The parts by id (<c>guitar</c>, <c>drums</c>, …), or <see langword="null"/> when the chart is malformed.</returns>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    public static IReadOnlyDictionary<string, LibraryDifficulties>? ReadParts(byte[] bytes, string storedName, ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var document = ChartReader.Read(bytes, storedName, options);
            var parts = new Dictionary<string, LibraryDifficulties>(StringComparer.Ordinal);
            foreach (var key in document.Tracks.Keys)
            {
                if (InstrumentParts.TryGetValue(key.Instrument, out string? part))
                    parts[part] = parts.GetValueOrDefault(part) | (LibraryDifficulties)(1 << (int)key.Difficulty);
            }

            if (ChartReader.IsMidi(storedName))
                AddUnreadParts(bytes, parts);

            return parts;
        }
        catch (ChartFormatException)
        {
            return null;
        }
    }

    /// <summary>Adds the MIDI file's parts the note reader skips, such as vocals, that have a note.</summary>
    private static void AddUnreadParts(byte[] bytes, Dictionary<string, LibraryDifficulties> parts)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(bytes);
        foreach (var chunk in chunks)
        {
            if (chunk.Id != "MTrk")
                continue;

            var events = MidiFile.ReadEvents(bytes, chunk);
            if (MidiFile.NameOf(bytes, events) is not { } name
                || MidiPartCatalog.PartOf(name) is not { IsKnown: true } part
                || MidiPartCatalog.InstrumentsOf(part).Count > 0
                || !HasNote(bytes, events))
            {
                continue;
            }

            var difficulties = ProStringParts.Contains(part.Id) ? ProStringDifficulties(bytes, events) : ProKeysDifficulties.GetValueOrDefault(name.Trim());
            parts[part.Id] = parts.GetValueOrDefault(part.Id) | difficulties;
        }
    }

    /// <summary>The difficulties a pro guitar or bass track has a string note for.</summary>
    private static LibraryDifficulties ProStringDifficulties(byte[] bytes, List<MidiEvent> events)
    {
        var difficulties = LibraryDifficulties.None;
        foreach (var e in events)
        {
            if (e.Kind != 0x90 || e.DataLength < 2 || bytes[e.DataStart + 1] == 0)
                continue;

            int pitch = bytes[e.DataStart];
            foreach (var (first, difficulty) in ProStringBases)
            {
                if (pitch >= first && pitch < first + ProStringCount)
                    difficulties |= difficulty;
            }
        }

        return difficulties;
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
}
