namespace ChartPacker.Core.Charts;

/// <summary>An instrument a chart can have notes for, in the order its highways are shown.</summary>
public enum ChartInstrument
{
    Guitar,
    Bass,
    Drums,
    Keys,
    Rhythm,
    GuitarCoop,
    GhlGuitar,
    GhlBass,
}

/// <summary>A chart's difficulty, easiest first.</summary>
public enum ChartDifficulty
{
    Easy,
    Medium,
    Hard,
    Expert,
}

/// <summary>How an instrument's notes are laid out.</summary>
public enum ChartInstrumentKind
{
    /// <summary>Five frets, green to orange, plus open notes.</summary>
    FiveFret,

    /// <summary>Four or five pads plus a kick.</summary>
    Drums,

    /// <summary>Guitar Hero Live's three columns of white and black frets, plus open notes.</summary>
    SixFret,
}

/// <summary>What a note is, beyond its lane.</summary>
[Flags]
public enum NoteFlags
{
    None = 0,

    /// <summary>A hammer-on or pull-off, natural or forced.</summary>
    Hopo = 1,

    /// <summary>A tap note.</summary>
    Tap = 2,

    /// <summary>A pro-drums cymbal rather than a tom.</summary>
    Cymbal = 4,

    /// <summary>An Expert+ second kick.</summary>
    DoubleKick = 8,

    /// <summary>Inside a star-power phrase.</summary>
    StarPower = 16,
}

/// <summary>One note of a chart.</summary>
/// <param name="Tick">Where it starts, in chart ticks.</param>
/// <param name="Length">How long it's held, in ticks; zero for a note without a sustain.</param>
/// <param name="Lane">
/// Its lane, from the left: frets green to orange; drum pads red to green; for six-fret, the three
/// white frets then the three black. <see cref="OpenLane"/> is an open note, or a drum kick.
/// </param>
public readonly record struct ChartNote(long Tick, long Length, int Lane, NoteFlags Flags)
{
    /// <summary>The lane of an open note, or of a drum kick.</summary>
    public const int OpenLane = -1;
}

/// <summary>A star-power phrase, in ticks.</summary>
public readonly record struct StarPowerPhrase(long Tick, long Length);

/// <summary>One instrument at one difficulty.</summary>
public readonly record struct ChartTrackKey(ChartInstrument Instrument, ChartDifficulty Difficulty);

/// <summary>The notes of one instrument at one difficulty, in tick order, then lane order.</summary>
public sealed class NoteTrack(IReadOnlyList<ChartNote> notes, IReadOnlyList<StarPowerPhrase> starPower)
{
    public IReadOnlyList<ChartNote> Notes { get; } = notes;

    public IReadOnlyList<StarPowerPhrase> StarPower { get; } = starPower;
}

/// <summary>What's read from a chart: its tempo map and the notes of each instrument and difficulty it has.</summary>
public sealed class ChartDocument(TempoMap tempo, IReadOnlyDictionary<ChartTrackKey, NoteTrack> tracks, TimeSpan? offset, bool isFiveLaneDrums)
{
    public TempoMap Tempo { get; } = tempo;

    /// <summary>The tracks that have at least one note.</summary>
    public IReadOnlyDictionary<ChartTrackKey, NoteTrack> Tracks { get; } = tracks;

    /// <summary>A <c>.chart</c>'s <c>[Song]</c> <c>Offset</c>, if it has one; a song.ini <c>delay</c> takes its place.</summary>
    public TimeSpan? Offset { get; } = offset;

    /// <summary>Whether the drums have five pads rather than four.</summary>
    public bool IsFiveLaneDrums { get; } = isFiveLaneDrums;

    /// <summary>The difficulties <paramref name="instrument"/> has notes for, easiest first.</summary>
    public IReadOnlyList<ChartDifficulty> DifficultiesOf(ChartInstrument instrument) =>
        Enum.GetValues<ChartDifficulty>().Where(d => Tracks.ContainsKey(new ChartTrackKey(instrument, d))).ToList();

    /// <summary>The instruments with notes at any difficulty, in display order.</summary>
    public IReadOnlyList<ChartInstrument> Instruments =>
        Enum.GetValues<ChartInstrument>().Where(i => DifficultiesOf(i).Count > 0).ToList();
}

/// <summary>Settings a chart doesn't carry itself.</summary>
/// <param name="ProDrums">song.ini's <c>pro_drums</c>: the MIDI drums' yellow, blue and green are cymbals unless marked as toms.</param>
public sealed record ChartReadOptions(bool ProDrums = false)
{
    public static ChartReadOptions Default { get; } = new();

    /// <summary>The options a song's metadata sets: <c>pro_drums</c>, or its legacy <c>pro_drum</c>.</summary>
    public static ChartReadOptions From(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        string? value = SongChartSettings.First(pairs, "pro_drums") ?? SongChartSettings.First(pairs, "pro_drum");
        return new ChartReadOptions(value is not null && (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1"));
    }
}

/// <summary>What a song's metadata says about how its chart plays.</summary>
public static class SongChartSettings
{
    /// <summary>
    /// How much later than its ticks say the chart plays against the audio: song.ini's
    /// <c>delay</c> in milliseconds when it's set, otherwise the chart's own <see cref="ChartDocument.Offset"/>.
    /// </summary>
    public static TimeSpan DelayOf(IReadOnlyList<KeyValuePair<string, string>> pairs, ChartDocument chart)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(chart);
        return long.TryParse(First(pairs, "delay"), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long ms)
            && Math.Abs(ms) < 86_400_000
            ? TimeSpan.FromMilliseconds(ms)
            : chart.Offset ?? TimeSpan.Zero;
    }

    internal static string? First(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
        pairs.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A chart can't be read or changed: it's malformed, or a change to it didn't check out.</summary>
public sealed class ChartFormatException(string message) : IOException(message);

/// <summary>Facts about each <see cref="ChartInstrument"/>.</summary>
public static class ChartInstruments
{
    /// <summary>How <paramref name="instrument"/>'s notes are laid out.</summary>
    public static ChartInstrumentKind KindOf(ChartInstrument instrument) => instrument switch
    {
        ChartInstrument.Drums => ChartInstrumentKind.Drums,
        ChartInstrument.GhlGuitar or ChartInstrument.GhlBass => ChartInstrumentKind.SixFret,
        _ => ChartInstrumentKind.FiveFret,
    };
}
