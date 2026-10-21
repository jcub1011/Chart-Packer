using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>What the author picks for a run of Generate star power (charter-spec C8 item 1).</summary>
/// <param name="SpacingSeconds">About how far apart phrases are, in seconds of the part's playing: its long rests don't count.</param>
/// <param name="PlanForSpikes">
/// Whether phrases are placed so a player has enough star power to activate going into each hard passage; otherwise
/// they're only spread evenly.
/// </param>
public sealed record StarPowerOptions(double SpacingSeconds = 22, bool PlanForSpikes = true)
{
    /// <summary>The spec's defaults: about 22 s apart, as the official charts are, planned for the hard passages.</summary>
    public static StarPowerOptions Default { get; } = new();

    /// <summary>The fewest seconds apart the author can ask for.</summary>
    public const double MinSpacingSeconds = 10;

    /// <summary>The most seconds apart the author can ask for.</summary>
    public const double MaxSpacingSeconds = 60;
}

/// <summary>
/// The constants of Generate star power (<see cref="StarPowerGenerator"/>), from the games' meter and from how charts
/// place phrases. Measures are as the time signatures in force have them, so a measure of 6/8 counts as one, as YARG
/// drains the meter.
/// </summary>
public sealed record StarPowerParameters
{
    /// <summary>How much of the meter a phrase gives: a quarter.</summary>
    public double MeterPerPhrase { get; init; } = 0.25;

    /// <summary>How much of the meter a player needs to activate: a half.</summary>
    public double ActivationThreshold { get; init; } = 0.5;

    /// <summary>How many measures a full meter lasts once activated.</summary>
    public double FullMeterMeasures { get; init; } = 8;

    /// <summary>The fewest phrases a whole part is given, a full meter's worth, where they fit.</summary>
    public int MinPhrases { get; init; } = 4;

    /// <summary>The longest stretch of playing, in seconds, left without a phrase where one fits.</summary>
    public double MaxGapSeconds { get; init; } = 45;

    /// <summary>How many phrases past the count asked for can be added to close a stretch longer than <see cref="MaxGapSeconds"/>.</summary>
    public int MaxExtraPhrases { get; init; } = 2;

    /// <summary>The fewest measures between one phrase's end and the next one's start: the official charts never have fewer than 2, and seldom fewer than 5.</summary>
    public double MinGapMeasures { get; init; } = 4;

    /// <summary>The most of the phrases asked for that go to charging the meter before hard passages; the rest spread out.</summary>
    public double SpikeShare { get; init; } = 0.75;

    /// <summary>The fewest chords a phrase covers on the difficulty it's placed against.</summary>
    public int MinChords { get; init; } = 4;

    /// <summary>The most chords a phrase covers on the difficulty it's placed against.</summary>
    public int MaxChords { get; init; } = 40;

    /// <summary>How many measures a phrase is at the most before it counts as long: the official charts' are mostly a measure.</summary>
    public double PreferredMaxMeasures { get; init; } = 1.25;

    /// <summary>How many measures a phrase is at the most, for a part too sparse to fill a shorter one.</summary>
    public double MaxMeasures { get; init; } = 4;

    /// <summary>How long a phrase is at the most, in seconds.</summary>
    public double MaxPhraseSeconds { get; init; } = 10;

    /// <summary>How many measures without a note make a rest, which isn't counted as the part's playing.</summary>
    public double RestMeasures { get; init; } = 4;

    /// <summary>How many seconds without a note make a rest, whatever the measures.</summary>
    public double RestSeconds { get; init; } = 8;

    /// <summary>The fewest measures of playing between a phrase that completes a charge and the hard passage.</summary>
    public double MinLeadMeasures { get; init; } = 0.5;

    /// <summary>The most measures of playing between a phrase that completes a charge and the hard passage.</summary>
    public double MaxLeadMeasures { get; init; } = 4;

    /// <summary>How many measures of playing before a hard passage a charge is best completed.</summary>
    public double IdealLeadMeasures { get; init; } = 1;

    /// <summary>How many measures of playing before a hard passage the first of a pair of phrases charging for it starts, at the most.</summary>
    public double PairSpanMeasures { get; init; } = 16;

    /// <summary>The fewest measures of playing after a phrase completes, so the star power it gives can be used.</summary>
    public double TailMeasures { get; init; } = 2;

    /// <summary>How many measures each stretch the difficulty is scored over spans, to find the hard passages.</summary>
    public int SpikeWindowMeasures { get; init; } = 2;

    /// <summary>How far a hard passage rises above what's around it, at the least, as a share of the part's range.</summary>
    public double MinProminenceShare { get; init; } = 0.15;

    /// <summary>How far a hard passage rises above what's around it, at the least, in the difficulty model's units.</summary>
    public double MinProminence { get; init; } = 5;

    /// <summary>The fewest measures between the starts of two hard passages; nearer ones count as one.</summary>
    public double MinSpikeSeparationMeasures { get; init; } = 8;

    /// <summary>The defaults.</summary>
    public static StarPowerParameters Default { get; } = new();
}

/// <summary>Why <see cref="StarPowerGenerator"/> proposed a phrase.</summary>
public enum StarPowerRole
{
    /// <summary>It completes, or starts, the charge a player activates going into a hard passage.</summary>
    SpikeLeadIn,

    /// <summary>It spreads the phrases over the part.</summary>
    Filler,
}

/// <summary>A star power phrase <see cref="StarPowerGenerator"/> proposes.</summary>
/// <param name="SpikeTick">For a <see cref="StarPowerRole.SpikeLeadIn"/>, where the hard passage it charges for starts.</param>
public readonly record struct StarPowerProposal(Phrase Phrase, StarPowerRole Role, long? SpikeTick);

/// <summary>A hard passage <see cref="StarPowerGenerator"/> found: where it starts rising, its peak, and how far it rises.</summary>
/// <param name="Prominence">How far it rises above what's around it, in the difficulty model's units.</param>
/// <param name="Peak">The difficulty at its peak.</param>
public readonly record struct StarPowerSpike(long StartTick, long PeakTick, double Prominence, double Peak);

/// <summary>Why <see cref="StarPowerGenerator.Propose"/> proposed no phrases.</summary>
public enum NoStarPowerReason
{
    /// <summary>It proposed some.</summary>
    None,

    /// <summary>The part isn't 5-fret or keys.</summary>
    NotFiveFret,

    /// <summary>The part has no notes.</summary>
    NoNotes,

    /// <summary>No spot fits: none has enough notes clear of sections, solos' edges and the phrases already there.</summary>
    NoSpotFits,
}

/// <summary>What <see cref="StarPowerGenerator.Propose"/> proposed, the hard passages it found, and the difficulty it read.</summary>
/// <param name="Source">The difficulty the phrases were placed against: the part's highest with notes.</param>
public sealed record StarPowerGeneration(IReadOnlyList<StarPowerProposal> Proposals, IReadOnlyList<StarPowerSpike> Spikes, ChartDifficulty? Source, NoStarPowerReason Reason);

/// <summary>
/// Generate star power (charter-spec C8 item 1): proposes star power phrases for a 5-fret or keys part, over a span of it
/// or all of it, which the author then keeps or drops (<see cref="Editing.StarPowerProposals"/>), or the library adds for
/// CHR066's fix (<see cref="Validation.SongDifficultyGeneration"/>). It changes nothing.
/// </summary>
/// <remarks>
/// <para>Phrases are placed against the part's highest difficulty with notes, and each covers a note on every difficulty
/// with notes, so none is empty on one (CHR065). The count is the part's playing, in seconds with its rests of
/// <see cref="StarPowerParameters.RestMeasures"/> measures or <see cref="StarPowerParameters.RestSeconds"/> left out,
/// over <see cref="StarPowerOptions.SpacingSeconds"/>, and at least <see cref="StarPowerParameters.MinPhrases"/> for a
/// whole part.</para>
/// <para>A phrase starts on a measure line, or on a run's first chord where it's on a beat, and ends at a run's end or
/// the last chord before a measure line; a run is chords between rests of a beat and sections (as the reducer splits
/// them). It covers its last chord's sustains, up to the next chord. It doesn't cross a section, a solo's edge or a big
/// rock ending, overlap star power already there, or complete too late for its star power to be used.</para>
/// <para>With <see cref="StarPowerOptions.PlanForSpikes"/>, the hard passages are found from the difficulty model,
/// scored a few measures at a time on their own so endurance doesn't rank later ones higher, and the most prominent
/// are charged for: the meter is simulated as the games fill and drain it, and each is given the phrases that bring a
/// player to half a meter just before it, after the activation before it has run out. The rest are spread over the
/// stretches between, so none is much longer than another, and any longer than
/// <see cref="StarPowerParameters.MaxGapSeconds"/> is closed where a phrase fits.</para>
/// </remarks>
public static class StarPowerGenerator
{
    /// <summary>Whether a part of <paramref name="kind"/> can be given star power: 5-fret, keys among them.</summary>
    public static bool CanGenerate(ChartInstrumentKind kind) => kind == ChartInstrumentKind.FiveFret;

    /// <summary>Whether <paramref name="part"/> has a star power phrase.</summary>
    public static bool HasStarPower(EditablePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        foreach (var phrase in part.Phrases.AsSpan())
        {
            if (phrase.Kind == PhraseKind.StarPower)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a star power phrase fits anywhere in <paramref name="part"/> of <paramref name="chart"/>, so
    /// <see cref="Propose"/> over the whole part proposes at least one: a spot with enough chords, clear of sections,
    /// solos' edges and the star power there, early enough to be used. It doesn't score the part's difficulty, so it's
    /// cheap enough for lint (CHR066).
    /// </summary>
    public static bool HasSpot(EditableChart chart, EditablePart part, StarPowerParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(part);
        if (!CanGenerate(part.Kind) || DifficultyGenerator.HighestCharted(part) is not { } highest)
            return false;
        var context = StarPowerContext.Of(chart, part, highest, null, parameters ?? StarPowerParameters.Default);
        return StarPowerCandidates.Find(context).Count > 0;
    }

    /// <summary>
    /// Proposes star power phrases for <paramref name="part"/> of <paramref name="chart"/>. Star power already there stays:
    /// proposals keep clear of it, and it counts towards the meter and the spacing.
    /// </summary>
    /// <param name="readOptions">How the song's HOPOs and sustains are read (the defaults when null).</param>
    /// <param name="span">The ticks a phrase must lie within; the whole part when null.</param>
    public static StarPowerGeneration Propose(EditableChart chart, ChartInstrument part, StarPowerOptions options, ChartReadOptions? readOptions = null, (long From, long To)? span = null, StarPowerParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SpacingSeconds, 1, nameof(options));
        var p = parameters ?? StarPowerParameters.Default;

        if (!CanGenerate(ChartInstruments.KindOf(part)))
            return new StarPowerGeneration([], [], null, NoStarPowerReason.NotFiveFret);
        if (chart.PartOf(part) is not { } whole || DifficultyGenerator.HighestCharted(whole) is not { } highest)
            return new StarPowerGeneration([], [], null, NoStarPowerReason.NoNotes);

        var context = StarPowerContext.Of(chart, whole, highest, span, p);
        var spikes = options.PlanForSpikes
            ? StarPowerSpikes.Find(context, DifficultyNotes.Of(GameTrack(chart, context.Source, readOptions ?? ChartReadOptions.Default), context.Map, DifficultyNotes.EndTickOf(chart)))
            : [];
        var candidates = StarPowerCandidates.Find(context);
        var proposals = new StarPowerScheduler(context, candidates, options).Schedule(spikes);
        return new StarPowerGeneration(proposals, spikes, highest, proposals.Count > 0 ? NoStarPowerReason.None : NoStarPowerReason.NoSpotFits);
    }

    /// <summary>The command that adds <paramref name="proposals"/> to <paramref name="part"/> as one undoable step; null for none.</summary>
    public static IChartCommand? CommandOf(ChartInstrument part, IEnumerable<StarPowerProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var adds = proposals.Select(p => (IChartCommand)new AddPhrase(part, p.Phrase)).ToList();
        return adds.Count switch
        {
            0 => null,
            1 => adds[0],
            _ => new CompositeCommand(adds),
        };
    }

    // The track as the games read it, which the difficulty model reads: HOPOs and taps as they're played.
    private static NoteTrack GameTrack(EditableChart chart, EditableTrack track, ChartReadOptions options) =>
        new(GameView.Frets(track.Notes, track.FlaggedChords, chart.Resolution, options, []), []);
}

/// <summary>What Generate star power reads of a chart, worked out once for each stage (<see cref="StarPowerGenerator"/>).</summary>
/// <param name="Source">The difficulty phrases are placed against: the part's highest with notes.</param>
/// <param name="Others">The part's other difficulties with notes, each of which a phrase must cover a note of.</param>
/// <param name="Chords">The source's chords, in tick order.</param>
/// <param name="Runs">The source's runs of chords between rests and sections, as the first and last chord of each.</param>
/// <param name="Sections">The ticks of the chart's sections.</param>
/// <param name="Span">The ticks a phrase must lie within; null for the whole part.</param>
/// <param name="From">The first tick of playing counted: the first chord's, or the span's start after it.</param>
/// <param name="To">The last tick of playing counted: the last chord's, or the span's end before it.</param>
internal sealed record StarPowerContext(
    EditablePart Part,
    EditableTrack Source,
    IReadOnlyList<EditableTrack> Others,
    List<(long Tick, EditableNote[] Notes)> Chords,
    List<(int Start, int End)> Runs,
    long[] Sections,
    TempoMap Map,
    StarPowerTimeline Timeline,
    StarPowerParameters Parameters,
    (long From, long To)? Span,
    long From,
    long To)
{
    public int Resolution => Map.Resolution;

    /// <summary>The tick of the source's last chord.</summary>
    public long LastChordTick => Chords[^1].Tick;

    public static StarPowerContext Of(EditableChart chart, EditablePart part, ChartDifficulty highest, (long From, long To)? span, StarPowerParameters p)
    {
        var source = part[highest];
        var others = part.Tracks.Where(t => t.Difficulty != highest && t.Notes.Count > 0).ToList();
        var chords = Reducer.ChordsOf([.. source.Notes]);
        var sections = Reducer.SectionTicks(chart);
        var map = chart.TempoMap.ToTempoMap();
        var runs = Reducer.Phrases(chords, sections, chart.Resolution);
        var timeline = new StarPowerTimeline(map, chords, p);
        long from = Math.Max(chords[0].Tick, span?.From ?? long.MinValue);
        long to = Math.Min(chords[^1].Tick, span?.To ?? long.MaxValue);
        return new StarPowerContext(part, source, others, chords, runs, sections, map, timeline, p, span, from, Math.Max(from, to));
    }
}
