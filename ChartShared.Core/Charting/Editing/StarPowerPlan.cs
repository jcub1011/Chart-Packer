using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What's wrong with a region the author drew or resized, as <see cref="StarPowerPlan.Validate"/> finds it.</summary>
[Flags]
public enum StarPowerRegionProblem
{
    /// <summary>It's fine.</summary>
    None = 0,

    /// <summary>It isn't a star power phrase.</summary>
    WrongKind = 1,

    /// <summary>It starts before the chart, or has no length.</summary>
    InvalidLength = 2,

    /// <summary>It reaches outside the span star power is proposed over.</summary>
    OutsideScope = 4,

    /// <summary>It overlaps a region that's kept, or star power that stays.</summary>
    Overlap = 8,

    /// <summary>It overlaps a big rock ending.</summary>
    CrossesBigRockEnding = 16,

    /// <summary>It has no notes on a difficulty with notes, which CHR065 warns of.</summary>
    EmptyOnDifficulty = 32,

    /// <summary>It crosses a section, which the generator never does.</summary>
    CrossesSection = 64,

    /// <summary>It reaches past a solo's edge, which the generator never does.</summary>
    CrossesSolo = 128,
}

/// <summary>The problems that stop a region being added: the rest are warnings.</summary>
public static class StarPowerRegionProblems
{
    /// <summary>The problems that stop a region being added.</summary>
    public const StarPowerRegionProblem Blocking =
        StarPowerRegionProblem.WrongKind
        | StarPowerRegionProblem.InvalidLength
        | StarPowerRegionProblem.OutsideScope
        | StarPowerRegionProblem.Overlap
        | StarPowerRegionProblem.CrossesBigRockEnding;

    /// <summary>Whether <paramref name="problems"/> stops a region being added.</summary>
    public static bool IsBlocked(this StarPowerRegionProblem problems) => (problems & Blocking) != 0;
}

/// <summary>A star power phrase of a <see cref="StarPowerPlan"/>: one the generator proposed, or one the author drew.</summary>
/// <param name="Kept">Whether it's added when the plan is applied; every region starts kept.</param>
/// <param name="Added">Whether the author drew it, rather than the generator proposing it.</param>
public readonly record struct StarPowerRegion(
    Phrase Phrase,
    StarPowerRole Role,
    long? SpikeTick,
    bool Kept = true,
    bool Added = false)
{
    /// <summary>Whether it charges for a hard passage.</summary>
    public bool IsSpikeLeadIn => Role == StarPowerRole.SpikeLeadIn;

    /// <summary>The tick after the last one it covers.</summary>
    public long CoverEnd => Phrase.CoverEnd;
}

/// <summary>
/// A star power layout worked out for one part by Generate star power (charter-spec C8 item 1), reviewed in its window:
/// the phrases proposed, each kept or dropped, ones the author draws, and the star power already there, until the kept
/// ones are applied to the chart as one undoable step. It changes nothing itself.
/// </summary>
/// <remarks>
/// Over the whole part it replaces the star power there by default (<see cref="StarPowerMode.Replace"/>), so its phrases
/// are worked out as if there were none; or it adds alongside them (<see cref="StarPowerMode.Add"/>), keeping clear of
/// them. Over a span, only the phrases wholly inside it are replaced; one reaching across its edge stays.
/// </remarks>
public sealed class StarPowerPlan
{
    private readonly List<StarPowerRegion> _regions;
    private readonly List<Phrase> _existing;
    private readonly List<Phrase> _surviving;
    private readonly EditableChart _chart;
    private readonly EditablePart? _part;
    private readonly StarPowerParameters _parameters;

    private StarPowerPlan(
        EditableChart chart,
        ChartInstrument part,
        EditablePart? whole,
        StarPowerOptions options,
        StarPowerMode mode,
        (long From, long To)? span,
        StarPowerParameters parameters,
        ChartDifficulty? source,
        List<StarPowerRegion> regions,
        List<Phrase> existing,
        IReadOnlyList<StarPowerSpike> spikes,
        NoStarPowerReason reason)
    {
        _chart = chart;
        _part = whole;
        _parameters = parameters;
        _regions = regions;
        _existing = existing;
        _surviving = [];
        if (whole is not null)
        {
            foreach (var phrase in whole.Phrases)
            {
                if (phrase.Kind == PhraseKind.StarPower && !(mode == StarPowerMode.Replace && existing.Contains(phrase)))
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
    public StarPowerMode Mode { get; }

    /// <summary>The ticks it proposes over; null for the whole part.</summary>
    public (long From, long To)? Span { get; }

    /// <summary>The regions, earliest first.</summary>
    public IReadOnlyList<StarPowerRegion> Regions => _regions;

    /// <summary>The star power the chart has wholly inside the span, which <see cref="StarPowerMode.Replace"/> replaces.</summary>
    public IReadOnlyList<Phrase> Existing => _existing;

    /// <summary>
    /// The star power that stays when the plan is applied: all of it in <see cref="StarPowerMode.Add"/>, and in
    /// <see cref="StarPowerMode.Replace"/> what reaches across the span's edge.
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
    /// in <see cref="StarPowerMode.Add"/>, a part that has star power already is refused, as the author's phrases are
    /// theirs to keep: they can replace them, or select a span to add beside them.
    /// </summary>
    public static StarPowerPlan Generate(
        EditableChart chart,
        ChartInstrument part,
        StarPowerOptions options,
        StarPowerMode mode,
        (long From, long To)? span,
        ChartReadOptions? readOptions = null,
        StarPowerParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        var p = parameters ?? StarPowerParameters.Default;
        var whole = chart.PartOf(part);
        if (whole is null || !StarPowerGenerator.CanGenerate(whole.Kind))
            return Empty(chart, part, whole, options, mode, span, p, NoStarPowerReason.NotFiveFret);
        if (DifficultyGenerator.HighestCharted(whole) is null)
            return Empty(chart, part, whole, options, mode, span, p, NoStarPowerReason.NoNotes);
        if (mode == StarPowerMode.Add && span is null && StarPowerGenerator.HasStarPower(whole))
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
    /// Adds a region the author drew, kept, earliest first.
    /// </summary>
    /// <returns>The index it went at; -1 when it can't be added (<see cref="Validate"/> says why).</returns>
    public int Add(Phrase phrase)
    {
        if (Validate(phrase).IsBlocked())
            return -1;
        int index = _regions.FindIndex(r => r.Phrase.Tick > phrase.Tick);
        if (index < 0)
            index = _regions.Count;
        _regions.Insert(index, new StarPowerRegion(phrase, StarPowerRole.Filler, null, Kept: true, Added: true));
        return index;
    }

    /// <summary>Removes the region at <paramref name="index"/>.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(int index)
    {
        if (index < 0 || index >= _regions.Count)
            return false;
        _regions.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Resizes the region at <paramref name="index"/> to start at <paramref name="tick"/> and last
    /// <paramref name="length"/> ticks, in place: <see cref="Sort"/> puts it back in time order once the drag is done.
    /// </summary>
    /// <returns>Its index; -1 when it can't be resized (<see cref="Validate"/> says why).</returns>
    public int Resize(int index, long tick, long length)
    {
        if (index < 0 || index >= _regions.Count)
            return -1;
        var phrase = _regions[index].Phrase with { Tick = tick, Length = length };
        if (Validate(phrase, index).IsBlocked())
            return -1;
        _regions[index] = _regions[index] with { Phrase = phrase };
        return index;
    }

    /// <summary>Puts the regions back in time order, after a resize has moved one past another.</summary>
    public void Sort() => _regions.Sort(static (x, y) => x.Phrase.Tick.CompareTo(y.Phrase.Tick));

    /// <summary>What's wrong with <paramref name="phrase"/> as a region here; <paramref name="ignoreIndex"/> doesn't count as overlapping.</summary>
    public StarPowerRegionProblem Validate(Phrase phrase, int? ignoreIndex = null)
    {
        if (phrase.Kind != PhraseKind.StarPower)
            return StarPowerRegionProblem.WrongKind;
        var problems = StarPowerRegionProblem.None;
        if (phrase.Tick < 0 || phrase.Length <= 0)
            problems |= StarPowerRegionProblem.InvalidLength;
        if (Span is { } span && (phrase.Tick < span.From || phrase.CoverEnd - 1 > span.To))
            problems |= StarPowerRegionProblem.OutsideScope;

        for (int i = 0; i < _regions.Count; i++)
        {
            if (i != ignoreIndex && _regions[i].Kept && phrase.Overlaps(_regions[i].Phrase))
                problems |= StarPowerRegionProblem.Overlap;
        }

        if (_part is not null)
        {
            foreach (var other in _part.Phrases)
            {
                if (other.Kind != PhraseKind.StarPower || !phrase.Overlaps(other))
                    continue;

                // In Replace mode the star power wholly in the span goes when the plan is applied, so overlapping it is
                // fine; one reaching across the span's edge stays, and can't be overlapped.
                if (Mode == StarPowerMode.Replace && _existing.Contains(other))
                    continue;
                problems |= StarPowerRegionProblem.Overlap;
            }

            foreach (var other in _part.Phrases)
            {
                if (!phrase.Overlaps(other))
                    continue;
                if (other.Kind == PhraseKind.BigRockEnding)
                    problems |= StarPowerRegionProblem.CrossesBigRockEnding;
                else if (other.Kind == PhraseKind.Solo && (phrase.Tick < other.Tick || phrase.CoverEnd > other.CoverEnd))
                    problems |= StarPowerRegionProblem.CrossesSolo;
            }

            foreach (var track in _part.Tracks)
            {
                if (track.Notes.Count > 0 && track.Notes.Between(phrase.Tick, phrase.CoverEnd - 1).IsEmpty)
                    problems |= StarPowerRegionProblem.EmptyOnDifficulty;
            }
        }

        // A section strictly after the start, before the end, as the generator keeps clear of them.
        var sections = Reducer.SectionTicks(_chart);
        int section = Array.BinarySearch(sections, phrase.Tick + 1);
        section = section >= 0 ? section : ~section;
        if (section < sections.Length && sections[section] < phrase.CoverEnd)
            problems |= StarPowerRegionProblem.CrossesSection;

        return problems;
    }

    /// <summary>
    /// The command that applies the plan: the star power wholly in the span removed in
    /// <see cref="StarPowerMode.Replace"/>, then the kept regions added, as one undoable step. Null when there's
    /// nothing to do.
    /// </summary>
    public IChartCommand? BuildCommand()
    {
        var commands = new List<IChartCommand>();
        if (Mode == StarPowerMode.Replace)
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
        StarPowerMode mode,
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
