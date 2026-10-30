using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>A fill of a <see cref="FillPlan"/>.</summary>
/// <param name="Proposal">The fill, and the tick star power is activated on.</param>
/// <param name="Kept">Whether it's added when the plan is applied; every region starts kept.</param>
public readonly record struct FillRegion(FillProposal Proposal, bool Kept = true)
{
    /// <summary>The fill's phrase.</summary>
    public Phrase Phrase => Proposal.Fill;

    /// <summary>The measure's first beat the fill ends on, where star power is activated.</summary>
    public long ActivationTick => Proposal.ActivationTick;

    /// <summary>The tick after the last one it covers.</summary>
    public long CoverEnd => Phrase.CoverEnd;
}

/// <summary>
/// A layout of drum fills worked out for one part by Generate fills (charter-spec C6 item 5), reviewed in its window: the
/// fills proposed, each kept or dropped, and the fills already there, until the kept ones are applied to the chart as one
/// undoable step. It changes nothing itself.
/// </summary>
/// <remarks>
/// Over the whole part it adds alongside the fills there by default (<see cref="GenerationMode.Add"/>), keeping clear of
/// them; or it replaces them (<see cref="GenerationMode.Replace"/>), so its fills are worked out as if there were none.
/// Over a span, only the fills wholly inside it are replaced; one reaching across its edge stays.
/// </remarks>
public sealed class FillPlan
{
    private readonly List<FillRegion> _regions;
    private readonly List<Phrase> _existing;
    private readonly List<Phrase> _surviving;

    private FillPlan(
        FillOptions options,
        GenerationMode mode,
        (long From, long To)? span,
        ChartDifficulty? source,
        List<FillRegion> regions,
        List<Phrase> existing,
        List<Phrase> surviving,
        NoFillsReason reason)
    {
        Options = options;
        Mode = mode;
        Span = span;
        Source = source;
        _regions = regions;
        _existing = existing;
        _surviving = surviving;
        Reason = reason;
    }

    /// <summary>The part the fills are for.</summary>
    public static ChartInstrument Part => ChartInstrument.Drums;

    /// <summary>The difficulty the fills were placed against: the part's highest with notes, or null with none.</summary>
    public ChartDifficulty? Source { get; }

    /// <summary>The options it was worked out with.</summary>
    public FillOptions Options { get; }

    /// <summary>What it does with the fills already there.</summary>
    public GenerationMode Mode { get; }

    /// <summary>The ticks it proposes over; null for the whole part.</summary>
    public (long From, long To)? Span { get; }

    /// <summary>The fills proposed, earliest first.</summary>
    public IReadOnlyList<FillRegion> Regions => _regions;

    /// <summary>The fills the chart has wholly inside the span, which <see cref="GenerationMode.Replace"/> replaces.</summary>
    public IReadOnlyList<Phrase> Existing => _existing;

    /// <summary>
    /// The fills that stay when the plan is applied: all of them in <see cref="GenerationMode.Add"/>, and in
    /// <see cref="GenerationMode.Replace"/> what reaches across the span's edge.
    /// </summary>
    public IReadOnlyList<Phrase> SurvivingFills => _surviving;

    /// <summary>Why no fill was proposed; <see cref="NoFillsReason.None"/> when some were.</summary>
    public NoFillsReason Reason { get; }

    /// <summary>Whether any fill was proposed.</summary>
    public bool HasProposals => _regions.Count > 0;

    /// <summary>How many of the fills are kept.</summary>
    public int KeptCount => _regions.Count(static r => r.Kept);

    /// <summary>Whether any fill is kept, so there's something to apply.</summary>
    public bool AnyKept => _regions.Any(static r => r.Kept);

    /// <summary>Works out a plan for the drums of <paramref name="chart"/>, every fill kept.</summary>
    public static FillPlan Generate(EditableChart chart, DrumsVariant variant, FillOptions options, GenerationMode mode, (long From, long To)? span)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var whole = chart.PartOf(Part);
        var generation = FillGenerator.Propose(chart, variant, options, span, mode);
        var regions = generation.Proposals
            .Select(static proposal => new FillRegion(proposal))
            .ToList();
        var existing = ExistingIn(whole, span);
        var surviving = SurvivingIn(whole, span, mode);
        return new FillPlan(
            options,
            mode,
            span,
            whole is { } charted ? DifficultyGenerator.HighestCharted(charted) : null,
            regions,
            existing,
            surviving,
            generation.Reason);
    }

    /// <summary>Keeps the fill at <paramref name="index"/> if it's dropped, or drops it if it's kept.</summary>
    public void Toggle(int index) => SetKept(index, !_regions[index].Kept);

    /// <summary>Keeps or drops the fill at <paramref name="index"/>.</summary>
    public void SetKept(int index, bool kept) => _regions[index] = _regions[index] with { Kept = kept };

    /// <summary>Keeps every fill.</summary>
    public void KeepAll()
    {
        for (int i = 0; i < _regions.Count; i++)
            _regions[i] = _regions[i] with { Kept = true };
    }

    /// <summary>Drops every fill.</summary>
    public void DropAll()
    {
        for (int i = 0; i < _regions.Count; i++)
            _regions[i] = _regions[i] with { Kept = false };
    }

    /// <summary>
    /// The command that applies the plan: the fills wholly in the span removed in <see cref="GenerationMode.Replace"/>,
    /// then the kept fills added, as one undoable step. Null when there's nothing to do.
    /// </summary>
    public IChartCommand? BuildCommand()
    {
        var commands = new List<IChartCommand>();
        if (Mode == GenerationMode.Replace)
            commands.AddRange(_existing.Select(phrase => (IChartCommand)new RemovePhrase(Part, phrase)));
        commands.AddRange(_regions.Where(static r => r.Kept).Select(static r => (IChartCommand)new AddPhrase(Part, r.Phrase)));
        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
    }

    // The fills wholly inside the span, or all of them for the whole part.
    private static List<Phrase> ExistingIn(EditablePart? part, (long From, long To)? span)
    {
        var phrases = new List<Phrase>();
        if (part is null)
            return phrases;
        foreach (var phrase in part.Phrases)
        {
            if (phrase.Kind != PhraseKind.DrumFill)
                continue;
            if (span is { } s && (phrase.Tick < s.From || phrase.CoverEnd - 1 > s.To))
                continue;
            phrases.Add(phrase);
        }

        return phrases;
    }

    // The fills that stay: every fill in Add, and in Replace those the plan doesn't replace.
    private static List<Phrase> SurvivingIn(EditablePart? part, (long From, long To)? span, GenerationMode mode)
    {
        var phrases = new List<Phrase>();
        if (part is null)
            return phrases;
        foreach (var phrase in part.Phrases)
        {
            if (phrase.Kind != PhraseKind.DrumFill)
                continue;
            if (mode == GenerationMode.Replace && IsInside(phrase, span))
                continue;
            phrases.Add(phrase);
        }

        return phrases;
    }

    // Whether a phrase lies wholly inside the span, or the span is the whole part.
    private static bool IsInside(Phrase phrase, (long From, long To)? span) =>
        span is not { } s || (phrase.Tick >= s.From && phrase.CoverEnd - 1 <= s.To);
}
