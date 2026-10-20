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

/// <summary>What the difficulty model (docs/difficulty-model.md) made of a five-fret track.</summary>
/// <param name="Rank">Its rank, continuous: 0 to 6 for charts written to be played, 7 and up for overcharts.</param>
/// <param name="Attributes">Each attribute's score over the whole track.</param>
public sealed record DifficultyRating(double Rank, DifficultyAttributes Attributes)
{
    /// <summary>Whether it's beyond realistic play: rank 7 or more.</summary>
    public bool IsOverchart => Rank >= RankCurve.FirstOverchart;
}

/// <summary>
/// A suggested <c>diff_*</c> tier, the score it came from, and what that was worked out from. For five-fret parts the tier
/// is the difficulty model's rank, 0 and up, and <see cref="Rating"/> says more; for drums and six-fret, it's from 0 to 6.
/// </summary>
/// <remarks>Scores are comparable between the difficulties of one part, not between kinds of part.</remarks>
public sealed record DifficultyEstimate(int Tier, double Score, DifficultyBasis Basis, DifficultyRating? Rating = null);

/// <summary>
/// The weights a drums or six-fret rating is scored by (charter-spec C18 item 3). They're set by hand in v1, to be tuned
/// against rated charts later (§14). Five-fret parts are rated by the difficulty model's own parameters
/// (<see cref="DifficultyParameters"/>).
/// </summary>
public static class DifficultyWeights
{
    /// <summary>How long a stretch the peak density is measured over, in seconds.</summary>
    public const double PeakWindowSeconds = 2;

    public const double FretPeak = 0.30;
    public const double FretAverage = 0.25;
    public const double FretChords = 2.0;
    public const double FretHopoTap = 1.5;

    public const double DrumPeak = 0.28;
    public const double DrumAverage = 0.22;
    public const double DrumChords = 1.5;
    public const double DrumKickIndependence = 2.0;

    /// <summary>The scores a tier starts at: a score past n of them is tier n.</summary>
    public static IReadOnlyList<double> TierThresholds { get; } = [1.5, 2.5, 3.5, 4.5, 5.5, 6.5];
}

/// <summary>
/// Suggests a <c>diff_*</c> tier for a part (charter-spec C18 item 3). It reads the game view, so natural HOPOs count as
/// the games play them. It's only a suggestion, shown with what it was based on.
/// </summary>
/// <remarks>
/// <para>Five-fret parts are rated by the difficulty model (<see cref="DifficultyAnalyzer"/>): how hard the strumming,
/// fretting, rhythm and patterns are moment by moment, and how long that goes on, scored by the hardest stretches.
/// Overcharts are rated 7 and up.</para>
/// <para>Drums and six-fret, which the model doesn't cover, are rated from peak and average notes per second, how many
/// chords there are, how many HOPOs and taps, and for drums how often the kick is played apart from the hands.</para>
/// <para>One <c>diff_*</c> key rates a whole part, so the part's rating is its highest charted difficulty's.</para>
/// </remarks>
public static class DifficultyEstimator
{
    /// <summary>
    /// The tier of one track as the games read it, or null when it has fewer than two chords, or none apart in time. Notes
    /// after <paramref name="endTick"/>, the <c>[end]</c> event, aren't rated.
    /// </summary>
    public static DifficultyEstimate? Estimate(NoteTrack track, TempoMap tempo, ChartInstrumentKind kind, long? endTick = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(tempo);
        var chords = track.Notes.Where(n => endTick is not { } end || n.Tick <= end).GroupBy(static n => n.Tick).OrderBy(static g => g.Key).Select(static g => g.ToList()).ToList();
        if (chords.Count < 2)
            return null;
        double[] times = [.. chords.Select(c => tempo.SecondsAt(c[0].Tick))];
        double seconds = times[^1] - times[0];
        if (seconds <= 0)
            return null;

        bool drums = kind == ChartInstrumentKind.Drums;
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
        if (kind == ChartInstrumentKind.FiveFret)
        {
            var profile = DifficultyAnalyzer.Analyze(track, tempo, endTick);
            return new DifficultyEstimate(profile.Tier, profile.ChartScore, basis, new DifficultyRating(profile.Rank, profile.ChartAttributes));
        }

        double score = drums
            ? (DifficultyWeights.DrumPeak * peak) + (DifficultyWeights.DrumAverage * average) + (DifficultyWeights.DrumChords * chordShare) + (DifficultyWeights.DrumKickIndependence * (kick ?? 0))
            : (DifficultyWeights.FretPeak * peak) + (DifficultyWeights.FretAverage * average) + (DifficultyWeights.FretChords * chordShare) + (DifficultyWeights.FretHopoTap * hopoTap);
        int tier = DifficultyWeights.TierThresholds.Count(t => score >= t);
        return new DifficultyEstimate(tier, score, basis);
    }

    /// <summary>Each charted difficulty's tier of <paramref name="part"/>, as the games read it with <paramref name="options"/>.</summary>
    public static IReadOnlyDictionary<ChartDifficulty, DifficultyEstimate> EstimateDifficulties(EditableChart chart, ChartInstrument part, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var estimates = new Dictionary<ChartDifficulty, DifficultyEstimate>();
        if (chart.PartOf(part) is not { } editable)
            return estimates;
        var tempo = chart.TempoMap.ToTempoMap();
        long? end = DifficultyNotes.EndTickOf(chart);
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            if (editable[difficulty].Notes.Count == 0)
                continue;
            var game = GameView.Project(chart, new ChartTrackKey(part, difficulty), options);
            if (Estimate(game, tempo, editable.Kind, end) is { } estimate)
                estimates[difficulty] = estimate;
        }

        return estimates;
    }

    /// <summary>The suggested rating of <paramref name="part"/>: its highest charted difficulty's, or null when it has none to rate.</summary>
    public static DifficultyEstimate? EstimatePart(EditableChart chart, ChartInstrument part, ChartReadOptions options)
    {
        var estimates = EstimateDifficulties(chart, part, options);
        return estimates.Count == 0 ? null : estimates[estimates.Keys.Max()];
    }

    // The most chords in any stretch of the window, per second.
    private static double Peak(double[] times)
    {
        int most = 0;
        int start = 0;
        for (int end = 0; end < times.Length; end++)
        {
            while (times[end] - times[start] > DifficultyWeights.PeakWindowSeconds)
                start++;
            most = Math.Max(most, end - start + 1);
        }

        return most / DifficultyWeights.PeakWindowSeconds;
    }
}
