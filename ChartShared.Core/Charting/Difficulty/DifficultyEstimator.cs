using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>What a suggested rating was worked out from (<see cref="DifficultyEstimator"/>), to be shown with it.</summary>
/// <param name="Chords">How many chords (notes on one tick) the track has.</param>
/// <param name="Seconds">From its first chord to its last.</param>
/// <param name="PeakNps">The most chords in any two seconds, per second.</param>
/// <param name="AverageNps">Chords per second, from the first to the last.</param>
/// <param name="ChordShare">The share of chords of two or more frets, or two or more pads (the kick aside).</param>
/// <param name="HopoTapShare">The share of chords that are HOPOs or taps, natural ones included; 0 for drums.</param>
/// <param name="KickIndependence">For drums, the share of kicks with no pad on their tick; null for frets, or no kicks.</param>
public sealed record DifficultyBasis(int Chords, double Seconds, double PeakNps, double AverageNps, double ChordShare, double HopoTapShare, double? KickIndependence);

/// <summary>What the difficulty model (docs/difficulty-model.md) made of a track.</summary>
/// <param name="Rank">Its rank, continuous: 0 to 6 for charts written to be played, 7 and up for overcharts.</param>
/// <param name="Attributes">Each attribute's score over the whole track.</param>
/// <param name="Instrument">The model it was rated with, which says what the attributes are (§14).</param>
public sealed record DifficultyRating(double Rank, DifficultyAttributes Attributes, DifficultyInstrument Instrument = DifficultyInstrument.FiveFret)
{
    /// <summary>Whether it's beyond realistic play: rank 7 or more.</summary>
    public bool IsOverchart => Rank >= RankCurve.FirstOverchart;
}

/// <summary>
/// A suggested <c>diff_*</c> tier, the score it came from, what the model made of the track, and what that was worked out
/// from. The tier is the difficulty model's rank, 0 and up, overcharts 7 and up.
/// </summary>
/// <remarks>Scores are comparable between the difficulties of one part and model, not between models.</remarks>
public sealed record DifficultyEstimate(int Tier, double Score, DifficultyBasis Basis, DifficultyRating Rating);

/// <summary>The suggested rating of one of a part's <c>diff_*</c> keys, and the model that rated it.</summary>
public sealed record KeyedEstimate(string Key, DifficultyInstrument Instrument, DifficultyEstimate Estimate);

/// <summary>
/// Suggests a <c>diff_*</c> tier for a part (charter-spec C18 item 3). It reads the game view, so natural HOPOs and the
/// drums' layout are as the games play them. It's only a suggestion, shown with what it was based on.
/// </summary>
/// <remarks>
/// <para>Every part is rated by the difficulty model (<see cref="DifficultyAnalyzer"/>), with its instrument's parameters
/// (<see cref="DifficultyParameters.For"/>): how hard the hands' and feet's work, the rhythm and the patterns are moment
/// by moment, and how long that goes on, scored by the hardest stretches. Overcharts are rated 7 and up.</para>
/// <para>One <c>diff_*</c> key rates a whole part, so the part's rating is its highest charted difficulty's. Pro drums have
/// two: <c>diff_drums</c>, rated as 4-lane drums play them, and <c>diff_drums_real</c>, with the cymbals.</para>
/// </remarks>
public static class DifficultyEstimator
{
    /// <summary>How long a stretch the peak density of <see cref="DifficultyBasis.PeakNps"/> is measured over, in seconds.</summary>
    public const double PeakWindowSeconds = 2;

    /// <summary>
    /// The tier of one track as the games read it, rated as <paramref name="instrument"/>, or null when it has fewer than
    /// two chords, or none apart in time. Notes after <paramref name="endTick"/>, the <c>[end]</c> event, aren't rated,
    /// nor counted; nor are Expert+ kicks, which the model doesn't read.
    /// </summary>
    /// <param name="freeLanes">For drums, the part's roll and trill lanes (<see cref="DifficultyNotes.FreeLanesOf"/>).</param>
    public static DifficultyEstimate? Estimate(NoteTrack track, TempoMap tempo, DifficultyInstrument instrument, long? endTick = null, IReadOnlyList<Phrase>? freeLanes = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(tempo);
        bool drums = instrument.IsDrums();
        var chords = track.Notes
            .Where(n => (endTick is not { } end || n.Tick <= end) && !(drums && (n.Flags & NoteFlags.DoubleKick) != 0))
            .GroupBy(static n => n.Tick).OrderBy(static g => g.Key).Select(static g => g.ToList()).ToList();
        if (chords.Count < 2)
            return null;
        double[] times = [.. chords.Select(c => tempo.SecondsAt(c[0].Tick))];
        double seconds = times[^1] - times[0];
        if (seconds <= 0)
            return null;

        double average = chords.Count / seconds;
        double peak = Peak(times);
        int chordCount = chords.Count(c => c.Count(n => n.Lane != ChartNote.OpenLane) >= 2);
        double chordShare = (double)chordCount / chords.Count;
        double hopoTap = drums ? 0 : (double)chords.Count(static c => c.Any(static n => (n.Flags & (NoteFlags.Hopo | NoteFlags.Tap)) != 0)) / chords.Count;
        double? kick = null;
        if (drums)
        {
            int kicks = 0, alone = 0;
            foreach (var chord in chords)
            {
                if (!chord.Any(static n => n.Lane == ChartNote.OpenLane))
                    continue;
                kicks++;
                if (chord.All(static n => n.Lane == ChartNote.OpenLane))
                    alone++;
            }

            kick = kicks > 0 ? (double)alone / kicks : null;
        }

        var basis = new DifficultyBasis(chords.Count, seconds, peak, average, chordShare, hopoTap, kick);
        var profile = DifficultyAnalyzer.Analyze(track, tempo, endTick, DifficultyParameters.For(instrument), freeLanes);
        return new DifficultyEstimate(profile.Tier, profile.ChartScore, basis, new DifficultyRating(profile.Rank, profile.ChartAttributes, instrument));
    }

    /// <summary>
    /// Each charted difficulty's tier of <paramref name="part"/>, as the games read it with <paramref name="options"/>,
    /// rated as <paramref name="instrument"/>: by default the part's own model, for drums the 4-lane view. None as Pro
    /// drums unless the games read the drums as Pro.
    /// </summary>
    public static IReadOnlyDictionary<ChartDifficulty, DifficultyEstimate> EstimateDifficulties(EditableChart chart, ChartInstrument part, ChartReadOptions options, DifficultyInstrument? instrument = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var model = instrument ?? DifficultyInstruments.Of(part);
        if (chart.PartOf(part) is not { } editable || !Rates(chart, options, model))
            return new Dictionary<ChartDifficulty, DifficultyEstimate>();
        return EstimateDifficulties(chart, editable, GameView.ProjectPart(chart, part, options), model);
    }

    /// <summary>
    /// The suggested rating of <paramref name="part"/>, rated as <paramref name="instrument"/> (by default its own model):
    /// its highest charted difficulty's, or null when it has none to rate, or it's to be rated as Pro drums and the games
    /// don't read the drums as Pro.
    /// </summary>
    public static DifficultyEstimate? EstimatePart(EditableChart chart, ChartInstrument part, ChartReadOptions options, DifficultyInstrument? instrument = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var model = instrument ?? DifficultyInstruments.Of(part);
        if (chart.PartOf(part) is not { } editable || !Rates(chart, options, model))
            return null;
        return EstimateHighest(chart, editable, GameView.ProjectPart(chart, part, options), model);
    }

    /// <summary>
    /// <paramref name="part"/>'s <c>diff_*</c> keys that can be rated, each with the model that rates it, as the games
    /// read <paramref name="chart"/> with <paramref name="options"/>: Pro drums' <c>diff_drums</c> as 4-lane drums, and
    /// <c>diff_drums_real</c> with the cymbals, but only when the games read the drums as Pro, as only then do they have
    /// cymbals.
    /// </summary>
    public static IReadOnlyList<(string Key, DifficultyInstrument Instrument)> KeyModelsOf(EditableChart chart, ChartInstrument part, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        return [.. DifficultyInstruments.KeysOf(part, chart.DrumsVariant).Where(k => Rates(chart, options, k.Instrument))];
    }

    /// <summary>
    /// The suggested rating of each of <paramref name="part"/>'s <c>diff_*</c> keys (<see cref="KeyModelsOf"/>), with the
    /// model that rates it. A key whose part has no difficulty to rate is left out.
    /// </summary>
    public static IReadOnlyList<KeyedEstimate> EstimateKeys(EditableChart chart, ChartInstrument part, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        if (chart.PartOf(part) is not { } editable)
            return [];
        var models = KeyModelsOf(chart, part, options);
        var tracks = GameView.ProjectPart(chart, part, options);
        var keyed = new List<KeyedEstimate>();
        foreach (var (key, instrument) in models)
        {
            if (EstimateHighest(chart, editable, tracks, instrument) is { } estimate)
                keyed.Add(new KeyedEstimate(key, instrument, estimate));
        }

        return keyed;
    }

    /// <summary>
    /// The suggested tier of every <c>diff_*</c> key <paramref name="chart"/>'s parts can be rated for
    /// (<see cref="EstimateKeys"/>), as the games read it with <paramref name="options"/>, ignoring case. A key whose part
    /// has no difficulty to rate is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, int> EstimateSong(EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var tiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in chart.Parts)
        {
            foreach (var keyed in EstimateKeys(chart, part.Instrument, options))
                tiers[keyed.Key] = keyed.Estimate.Tier;
        }

        return tiers;
    }

    /// <summary>
    /// The rating of the highest of <paramref name="tracks"/> that has one, rated as <paramref name="instrument"/>, and
    /// its difficulty; null when none has. Each is a difficulty's game view and its roll and trill lanes
    /// (<see cref="DifficultyNotes.FreeLanesOf"/>), in any order; those below the one rated aren't rated.
    /// </summary>
    public static (ChartDifficulty Difficulty, DifficultyEstimate Estimate)? EstimateHighest(
        IEnumerable<(ChartDifficulty Difficulty, NoteTrack Track, IReadOnlyList<Phrase> FreeLanes)> tracks, TempoMap tempo, DifficultyInstrument instrument, long? endTick = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        foreach (var (difficulty, track, freeLanes) in tracks.OrderByDescending(static t => t.Difficulty))
        {
            if (Estimate(track, tempo, instrument, endTick, freeLanes) is { } estimate)
                return (difficulty, estimate);
        }

        return null;
    }

    /// <summary>
    /// What <see cref="EstimateHighest(IEnumerable{ValueTuple{ChartDifficulty, NoteTrack, IReadOnlyList{Phrase}}}, TempoMap, DifficultyInstrument, long?)"/>
    /// rates of <paramref name="part"/>: each difficulty it has notes for, with its game view from <paramref name="gameTrack"/>
    /// and its roll and trill lanes. Read on the thread the chart is edited on, so they can be rated on another.
    /// </summary>
    public static List<(ChartDifficulty Difficulty, NoteTrack Track, IReadOnlyList<Phrase> FreeLanes)> ChartedTracks(EditablePart part, Func<ChartDifficulty, NoteTrack> gameTrack)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(gameTrack);
        return [.. Enum.GetValues<ChartDifficulty>().Where(d => part[d].Notes.Count > 0).Select(d => (d, gameTrack(d), DifficultyNotes.FreeLanesOf(part, d)))];
    }

    private static DifficultyEstimate? EstimateHighest(EditableChart chart, EditablePart editable, IReadOnlyDictionary<ChartDifficulty, NoteTrack> tracks, DifficultyInstrument instrument) =>
        EstimateHighest(
            ChartedTracks(editable, d => tracks[d]),
            chart.TempoMap.ToTempoMap(),
            instrument,
            DifficultyNotes.EndTickOf(chart))?.Estimate;

    // Whether chart can be rated as instrument: as Pro drums only when the games read its drums as Pro, as the game view
    // has cymbals only then, and DrumPads lays out Pro drums' pads only.
    private static bool Rates(EditableChart chart, ChartReadOptions options, DifficultyInstrument instrument) =>
        instrument != DifficultyInstrument.ProDrums || GameView.IsProDrums(chart, options);

    private static Dictionary<ChartDifficulty, DifficultyEstimate> EstimateDifficulties(EditableChart chart, EditablePart editable, IReadOnlyDictionary<ChartDifficulty, NoteTrack> tracks, DifficultyInstrument instrument)
    {
        var estimates = new Dictionary<ChartDifficulty, DifficultyEstimate>();
        var tempo = chart.TempoMap.ToTempoMap();
        long? end = DifficultyNotes.EndTickOf(chart);
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            if (editable[difficulty].Notes.Count == 0)
                continue;
            if (Estimate(tracks[difficulty], tempo, instrument, end, DifficultyNotes.FreeLanesOf(editable, difficulty)) is { } estimate)
                estimates[difficulty] = estimate;
        }

        return estimates;
    }

    // The most chords in any stretch of the window, per second.
    private static double Peak(double[] times)
    {
        int most = 0;
        int start = 0;
        for (int end = 0; end < times.Length; end++)
        {
            while (times[end] - times[start] > PeakWindowSeconds)
                start++;
            most = Math.Max(most, end - start + 1);
        }

        return most / PeakWindowSeconds;
    }
}
