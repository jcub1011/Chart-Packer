using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>A star power phrase of a <see cref="StarPowerPlan"/>.</summary>
/// <param name="Kept">Whether it's added when the plan is applied; every region starts kept.</param>
public readonly record struct StarPowerRegion(
    Phrase Phrase,
    StarPowerRole Role,
    long? SpikeTick,
    bool Kept = true)
{
    /// <summary>Whether it charges for a hard passage.</summary>
    public bool IsSpikeLeadIn => Role == StarPowerRole.SpikeLeadIn;

    /// <summary>The tick after the last one it covers.</summary>
    public long CoverEnd => Phrase.CoverEnd;
}

/// <summary>
/// A star power layout worked out for one part by Generate star power (charter-spec C8 item 1), reviewed in its window:
/// the phrases proposed, each kept or dropped, and the star power already there, until the kept ones are applied to the
/// chart as one undoable step. It changes nothing itself.
/// </summary>
/// <remarks>
/// Over the whole part it replaces the star power there by default (<see cref="GenerationMode.Replace"/>), so its phrases
/// are worked out as if there were none; or it adds alongside them (<see cref="GenerationMode.Add"/>), keeping clear of
/// them. Over a span, only the phrases wholly inside it are replaced; one reaching across its edge stays.
/// </remarks>
public sealed class StarPowerPlan
{
    private readonly List<StarPowerRegion> _regions;
    private readonly List<Phrase> _existing;
    private readonly List<Phrase> _surviving;
    private readonly EditableChart _chart;
    private readonly StarPowerParameters _parameters;

    private StarPowerPlan(
        EditableChart chart,
        ChartInstrument part,
        EditablePart? whole,
        StarPowerOptions options,
        GenerationMode mode,
        (long From, long To)? span,
        StarPowerParameters parameters,
        ChartDifficulty? source,
        List<StarPowerRegion> regions,
        List<Phrase> existing,
        IReadOnlyList<StarPowerSpike> spikes,
        NoStarPowerReason reason)
    {
        _chart = chart;
        _parameters = parameters;
        _regions = regions;
        _existing = existing;
        _surviving = [];
        if (whole is not null)
        {
            foreach (var phrase in whole.Phrases)
            {
                if (phrase.Kind == PhraseKind.StarPower && !(mode == GenerationMode.Replace && existing.Contains(phrase)))
                    _surviving.Add(phrase);
            }
        }

        Part = part;
        Options = options;
        Mode = mode;
        Span = span;
        Source = source;
        Spikes = spikes;
        Reason = reason;
    }

    /// <summary>The part the layout is for.</summary>
    public ChartInstrument Part { get; }

    /// <summary>The difficulty the phrases were placed against: the part's highest with notes, or null with none.</summary>
    public ChartDifficulty? Source { get; }

    /// <summary>The options it was worked out with.</summary>
    public StarPowerOptions Options { get; }

    /// <summary>What it does with the star power already there.</summary>
    public GenerationMode Mode { get; }

    /// <summary>The ticks it proposes over; null for the whole part.</summary>
    public (long From, long To)? Span { get; }

    /// <summary>The regions, earliest first.</summary>
    public IReadOnlyList<StarPowerRegion> Regions => _regions;

    /// <summary>The star power the chart has wholly inside the span, which <see cref="GenerationMode.Replace"/> replaces.</summary>
    public IReadOnlyList<Phrase> Existing => _existing;

    /// <summary>
    /// The star power that stays when the plan is applied: all of it in <see cref="GenerationMode.Add"/>, and in
    /// <see cref="GenerationMode.Replace"/> what reaches across the span's edge.
    /// </summary>
    public IReadOnlyList<Phrase> SurvivingStarPower => _surviving;

    /// <summary>The hard passages the difficulty model found, most prominent first.</summary>
    public IReadOnlyList<StarPowerSpike> Spikes { get; }

    /// <summary>Why no region was proposed; <see cref="NoStarPowerReason.None"/> when some were.</summary>
    public NoStarPowerReason Reason { get; }

    /// <summary>Whether any region was proposed or drawn.</summary>
    public bool HasProposals => _regions.Count > 0;

    /// <summary>How many of the regions are kept.</summary>
    public int KeptCount => _regions.Count(static r => r.Kept);

    /// <summary>Whether any region is kept, so there's something to apply.</summary>
    public bool AnyKept => _regions.Any(static r => r.Kept);

    /// <summary>
    /// Works out a layout for <paramref name="part"/> of <paramref name="chart"/>, every region kept. Over the whole part
    /// in <see cref="GenerationMode.Add"/>, a part that has star power already is refused, as the author's phrases are
    /// theirs to keep: they can replace them, or select a span to add beside them.
    /// </summary>
    public static StarPowerPlan Generate(
        EditableChart chart,
        ChartInstrument part,
        StarPowerOptions options,
        GenerationMode mode,
        (long From, long To)? span,
        ChartReadOptions? readOptions = null,
        StarPowerParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var p = parameters ?? StarPowerParameters.Default;
        var whole = chart.PartOf(part);
        if (whole is null || !StarPowerGenerator.CanGenerate(whole.Kind))
            return Empty(chart, part, whole, options, mode, span, p, NoStarPowerReason.UnsupportedInstrument);
        if (DifficultyGenerator.HighestCharted(whole) is null)
            return Empty(chart, part, whole, options, mode, span, p, NoStarPowerReason.NoNotes);
        if (mode == GenerationMode.Add && span is null && StarPowerGenerator.HasStarPower(whole))
            return Empty(chart, part, whole, options, mode, span, p, NoStarPowerReason.HasStarPower);

        var generation = StarPowerGenerator.Propose(chart, part, options, readOptions, span, p, mode);
        var regions = generation.Proposals
            .Select(static proposal => new StarPowerRegion(proposal.Phrase, proposal.Role, proposal.SpikeTick))
            .ToList();
        IReadOnlyList<StarPowerSpike> spikes = span is { } limits
            ? [.. generation.Spikes.Where(spike => spike.StartTick >= limits.From && spike.StartTick <= limits.To)]
            : generation.Spikes;
        return new StarPowerPlan(chart, part, whole, options, mode, span, p, generation.Source, regions, ExistingIn(whole, span), spikes, generation.Reason);
    }

    /// <summary>Keeps the region at <paramref name="index"/> if it's dropped, or drops it if it's kept.</summary>
    public void Toggle(int index) => SetKept(index, !_regions[index].Kept);

    /// <summary>Keeps or drops the region at <paramref name="index"/>.</summary>
    public void SetKept(int index, bool kept) => _regions[index] = _regions[index] with { Kept = kept };

    /// <summary>Keeps every region.</summary>
    public void KeepAll()
    {
        for (int i = 0; i < _regions.Count; i++)
            _regions[i] = _regions[i] with { Kept = true };
    }

    /// <summary>Drops every region.</summary>
    public void DropAll()
    {
        for (int i = 0; i < _regions.Count; i++)
            _regions[i] = _regions[i] with { Kept = false };
    }

    /// <summary>
    /// The command that applies the plan: the star power wholly in the span removed in
    /// <see cref="GenerationMode.Replace"/>, then the kept regions added, as one undoable step. Null when there's
    /// nothing to do.
    /// </summary>
    public IChartCommand? BuildCommand()
    {
        var commands = new List<IChartCommand>();
        if (Mode == GenerationMode.Replace)
            commands.AddRange(_existing.Select(phrase => (IChartCommand)new RemovePhrase(Part, phrase)));
        commands.AddRange(_regions.Where(static r => r.Kept).Select(r => (IChartCommand)new AddPhrase(Part, r.Phrase)));
        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
    }

    /// <summary>
    /// The meter and the hard passages' coverage as the kept regions would play: what the window's meter ribbon shows.
    /// </summary>
    public StarPowerCoverage Coverage(int samples = 512) =>
        StarPowerCoverageCalculator.Of(_chart, Part, _regions, Spikes, _parameters, samples, _surviving);

    private static StarPowerPlan Empty(
        EditableChart chart,
        ChartInstrument part,
        EditablePart? whole,
        StarPowerOptions options,
        GenerationMode mode,
        (long From, long To)? span,
        StarPowerParameters parameters,
        NoStarPowerReason reason) =>
        new(chart, part, whole, options, mode, span, parameters, whole is { } charted ? DifficultyGenerator.HighestCharted(charted) : null, [], ExistingIn(whole, span), [], reason);

    private static List<Phrase> ExistingIn(EditablePart? part, (long From, long To)? span)
    {
        var phrases = new List<Phrase>();
        if (part is null)
            return phrases;
        foreach (var phrase in part.Phrases)
        {
            if (phrase.Kind != PhraseKind.StarPower)
                continue;
            if (span is { } s && (phrase.Tick < s.From || phrase.CoverEnd - 1 > s.To))
                continue;
            phrases.Add(phrase);
        }

        return phrases;
    }
}
