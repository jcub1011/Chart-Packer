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
    GhlRhythm,
    GhlCoop,
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

    /// <summary>
    /// A drum accent, hit harder: a pad note, never the kick. Read from a MIDI chart only on a drums track with
    /// <c>[ENABLE_CHART_DYNAMICS]</c>.
    /// </summary>
    Accent = 32,

    /// <summary>
    /// A drum ghost note, hit softer: a pad note, never the kick. Read from a MIDI chart only on a drums track with
    /// <c>[ENABLE_CHART_DYNAMICS]</c>.
    /// </summary>
    Ghost = 64,
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

    /// <summary>Whether the drums are pro drums, whose yellow, blue and green can be cymbals.</summary>
    public bool IsProDrums { get; init; }

    /// <summary>The difficulties <paramref name="instrument"/> has notes for, easiest first.</summary>
    public IReadOnlyList<ChartDifficulty> DifficultiesOf(ChartInstrument instrument) =>
        [.. Enum.GetValues<ChartDifficulty>().Where(d => Tracks.ContainsKey(new ChartTrackKey(instrument, d)))];

    /// <summary>The instruments with notes at any difficulty, in display order.</summary>
    public IReadOnlyList<ChartInstrument> Instruments =>
        [.. Enum.GetValues<ChartInstrument>().Where(i => DifficultiesOf(i).Count > 0)];
}

/// <summary>The settings in a song's metadata that change how its chart is read, as YARG reads them.</summary>
/// <param name="ProDrums">
/// song.ini's <c>pro_drums</c> (or <c>pro_drum</c>): whether the drums are pro drums. Unset, a MIDI chart's
/// are, and a <c>.chart</c>'s are only with cymbal markers; see <see cref="DrumsLayouts"/>.
/// </param>
/// <param name="SustainCutoff">
/// song.ini's <c>sustain_cutoff_threshold</c>, in ticks: a note held for less is a note, not a sustain. Without it a
/// MIDI chart's is a third of a beat, and a <c>.chart</c> has none.
/// </param>
/// <param name="HopoFrequency">song.ini's <c>hopo_frequency</c>, in ticks: how close a note must follow the one before to be a natural HOPO.</param>
/// <param name="FiveLaneDrums">song.ini's <c>five_lane_drums</c>: whether the drums have five pads, rather than as the chart's notes say.</param>
/// <param name="EighthNoteHopo">song.ini's <c>eighthnote_hopo</c>, used when <paramref name="HopoFrequency"/> isn't: a threshold of half a beat, or a third.</param>
/// <param name="HopoFreq">song.ini's legacy <c>hopofreq</c>, 0 to 5, used when neither is: a threshold of 1/24 to 1/4 of a whole note.</param>
/// <param name="MultiplierNote">song.ini's <c>multiplier_note</c> (or <c>star_power_note</c>): a MIDI chart's star power is on 103 rather than 116 when it's 103.</param>
public sealed record ChartReadOptions(
    bool? ProDrums = null,
    long? SustainCutoff = null,
    long? HopoFrequency = null,
    bool? FiveLaneDrums = null,
    bool? EighthNoteHopo = null,
    int? HopoFreq = null,
    int? MultiplierNote = null)
{
    // hopofreq 0 to 5: the threshold is four beats over these.
    private static readonly int[] HopoFreqDivisors = [24, 16, 12, 8, 6, 4];

    public static ChartReadOptions Default { get; } = new();

    /// <summary>
    /// The exact natural-HOPO threshold a MIDI chart is read with, in place of <see cref="MidiHopoThresholds"/>'s
    /// first: for checking a chart reads the same under each game's threshold.
    /// </summary>
    internal long? MidiHopoTicks { get; init; }

    /// <summary>Whether any song.ini key sets the natural-HOPO threshold.</summary>
    private bool HasHopoSetting => HopoFrequency > 0 || EighthNoteHopo is not null || HopoFreq is >= 0 and < 6;

    /// <summary>
    /// The natural-HOPO threshold YARG gives a chart of <paramref name="resolution"/>: <see cref="HopoFrequency"/>
    /// when it's above 0; otherwise <see cref="EighthNoteHopo"/>'s half or third of a beat, <see cref="HopoFreq"/>'s
    /// fraction of four beats, or a third of a beat, which for a <c>.chart</c> is a tick longer for every 192 of a beat.
    /// A note is a natural HOPO no further than this from the one before; YARG gives a MIDI chart a tick more.
    /// </summary>
    /// <remarks>YARG won't load a song with any other <c>hopofreq</c>; it's taken here as unset.</remarks>
    internal long HopoThreshold(int resolution, bool chart)
    {
        if (HopoFrequency is { } ticks && ticks > 0)
            return ticks;

        long threshold = EighthNoteHopo is { } eighth ? resolution / (eighth ? 2 : 3)
            : HopoFreq is >= 0 and < 6 ? 4L * resolution / HopoFreqDivisors[HopoFreq.Value]
            : resolution / 3;
        return chart ? threshold + (long)(resolution / 192f) : threshold;
    }

    /// <summary>
    /// The natural-HOPO thresholds a MIDI chart of <paramref name="resolution"/> can be played with, YARG's
    /// first: its threshold and a tick, then without the tick, or, when song.ini sets none, Rock Band's 170/480 of a beat.
    /// </summary>
    internal long[] MidiHopoThresholds(int resolution)
    {
        long threshold = HopoThreshold(resolution, chart: false);
        return [threshold + 1, HasHopoSetting ? threshold : (long)(resolution * 170.0 / 480)];
    }

    /// <summary>The pitch of a MIDI chart's star power: 103 when <see cref="MultiplierNote"/> says so, otherwise 116.</summary>
    internal int MidiStarPowerPitch => MultiplierNote == 103 ? 103 : 116;

    /// <summary>
    /// The options a song's metadata sets, read as YARG reads them (see <see cref="SongIniValues"/>): <c>pro_drums</c>
    /// or <c>pro_drum</c>, <c>sustain_cutoff_threshold</c>, <c>hopo_frequency</c>, <c>five_lane_drums</c>,
    /// <c>eighthnote_hopo</c>, <c>hopofreq</c>, and <c>multiplier_note</c> or <c>star_power_note</c>.
    /// </summary>
    public static ChartReadOptions From(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        int? Int32(params string[] keys) => (int?)SongIniValues.Integer(SongIniValues.Last(pairs, keys), int.MinValue, int.MaxValue);
        return new ChartReadOptions(
            SongIniValues.Boolean(SongIniValues.Last(pairs, "pro_drum", "pro_drums")),
            SongIniValues.Integer(SongIniValues.Last(pairs, "sustain_cutoff_threshold")),
            SongIniValues.Integer(SongIniValues.Last(pairs, "hopo_frequency")),
            SongIniValues.Boolean(SongIniValues.Last(pairs, "five_lane_drums")),
            SongIniValues.Boolean(SongIniValues.Last(pairs, "eighthnote_hopo")),
            Int32("hopofreq"),
            Int32("multiplier_note", "star_power_note"));
    }
}

/// <summary>How a chart's drums are laid out.</summary>
internal enum DrumsLayout
{
    /// <summary>Four pads without cymbals.</summary>
    FourLane,

    /// <summary>Four pads; yellow, blue and green can be cymbals.</summary>
    Pro,

    /// <summary>Five pads.</summary>
    FiveLane,
}

/// <summary>
/// Works out a chart's <see cref="DrumsLayout"/> as YARG does when it scans a song: song.ini's
/// <c>five_lane_drums</c> settles it when given; otherwise whichever comes first of a fifth-pad note and
/// a pro-drums marker (a <c>.chart</c>'s cymbal marker, a MIDI chart's tom marker), unless
/// <c>pro_drums</c> already rules out five pads. Without either, MIDI drums are pro unless
/// <c>pro_drums</c> is false, and a <c>.chart</c>'s only when it's true.
/// </summary>
internal sealed class DrumsLayouts
{
    [Flags]
    private enum Possible
    {
        FourLane = 1,
        Pro = 2,
        FiveLane = 4,
    }

    private Possible _possible;

    public DrumsLayouts(ChartReadOptions options, bool midi)
    {
        _possible = options.FiveLaneDrums switch
        {
            true => Possible.FiveLane,
            false => Possible.FourLane,
            null => Possible.FourLane | Possible.FiveLane,
        };
        if (_possible != Possible.FiveLane && (midi ? options.ProDrums != false : options.ProDrums == true))
            _possible = (_possible | Possible.Pro) & ~Possible.FourLane;
    }

    /// <summary>A note on the fifth pad: five pads, if they're still possible.</summary>
    public void FifthPad()
    {
        if (_possible.HasFlag(Possible.FiveLane))
            _possible = Possible.FiveLane;
    }

    /// <summary>A cymbal or tom marker: pro drums, unless it's settled that there are five pads.</summary>
    public void ProMarker()
    {
        if (_possible != Possible.FiveLane)
            _possible = Possible.Pro;
    }

    /// <summary>Whether a fifth-pad note can still settle it.</summary>
    public bool FiveLanePossible => _possible.HasFlag(Possible.FiveLane);

    public DrumsLayout Layout =>
        _possible.HasFlag(Possible.FourLane) ? DrumsLayout.FourLane
        : _possible.HasFlag(Possible.Pro) ? DrumsLayout.Pro
        : DrumsLayout.FiveLane;
}

/// <summary>What a song's metadata says about how its chart plays.</summary>
public static class SongChartSettings
{
    /// <summary>
    /// How much later than its ticks say the chart plays against the audio, as YARG takes it: song.ini's
    /// <c>delay</c> in milliseconds unless it's 0, then its <c>delay_seconds</c>, then the chart's own
    /// <see cref="ChartDocument.Offset"/>.
    /// </summary>
    public static TimeSpan DelayOf(IReadOnlyList<KeyValuePair<string, string>> pairs, ChartDocument chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return DelayOf(pairs, chart.Offset);
    }

    /// <summary>As <see cref="DelayOf(IReadOnlyList{KeyValuePair{string, string}}, ChartDocument)"/>, given the chart's own offset rather than the chart.</summary>
    public static TimeSpan DelayOf(IReadOnlyList<KeyValuePair<string, string>> pairs, TimeSpan? chartOffset)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (SongIniValues.Integer(SongIniValues.Last(pairs, "delay")) is { } ms && ms != 0)
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, -MaxDelayMilliseconds, MaxDelayMilliseconds));
        if (SongIniValues.Real(SongIniValues.Last(pairs, "delay_seconds")) is { } seconds)
            return TimeSpan.FromMilliseconds((long)Math.Clamp(seconds * 1000, -MaxDelayMilliseconds, MaxDelayMilliseconds));
        return chartOffset ?? TimeSpan.Zero;
    }

    /// <summary>The furthest a <c>delay</c> can be either way, in milliseconds, and still be read: just under a day.</summary>
    internal const long MaxDelayMilliseconds = 86_399_999;

    /// <summary>A <c>delay</c> value in milliseconds, or <see langword="null"/> when it isn't one.</summary>
    internal static long? ParseDelay(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long ms)
        && Math.Abs(ms) <= MaxDelayMilliseconds
            ? ms
            : null;

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
        ChartInstrument.GhlGuitar or ChartInstrument.GhlBass or ChartInstrument.GhlRhythm or ChartInstrument.GhlCoop => ChartInstrumentKind.SixFret,
        _ => ChartInstrumentKind.FiveFret,
    };
}
