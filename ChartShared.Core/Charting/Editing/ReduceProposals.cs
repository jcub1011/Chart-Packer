using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>One difficulty's draft in a run of Reduce: what it's reduced from, the draft, and the draft as a track to draw.</summary>
/// <param name="Ghost">The draft as a track of its own, for the lane to draw as a ghost; it's no part of the chart.</param>
public sealed record ReductionDraft(ChartDifficulty Source, ChartTrackKey Target, ReductionResult Result, EditableTrack Ghost);

/// <summary>
/// A run of Reduce (charter-spec C9 item 2): a draft of one lower difficulty or more, each shown as a ghost over its
/// difficulty until they're added as one undoable step or the run is cancelled. The session owns it
/// (<see cref="EditorSession.Reduce"/>).
/// </summary>
/// <remarks>
/// The drafts are worked out from the chart as it was, so any change to the chart but the run's own ends the run, and the
/// status says so. A gesture's preview doesn't, as it's taken back or committed. Starting again with other rules, as the
/// Reduce dialog does on each change, replaces the drafts. A run of several difficulties reduces each from the one above it
/// as it will be, so Medium is made from Hard's draft, or from Hard as charted when Hard isn't in the run.
/// </remarks>
public sealed class ReduceProposals
{
    private const string NoSourceNotes = "There are no notes to reduce.";
    private const string Dropped = "The reduced draft was dropped, as the chart changed. Reduce again to see it.";

    private readonly EditorSession _session;
    private bool _accepting;

    internal ReduceProposals(EditorSession session) => _session = session;

    /// <summary>Whether a run is going on: there's a draft to add.</summary>
    public bool IsActive => Drafts.Count > 0;

    /// <summary>The part the run is of.</summary>
    public ChartInstrument? Part { get; private set; }

    /// <summary>The ticks the run reduces, both ends in, or null for the whole part.</summary>
    public (long From, long To)? Range { get; private set; }

    /// <summary>The drafts, highest difficulty first.</summary>
    public IReadOnlyList<ReductionDraft> Drafts { get; private set; } = [];

    /// <summary>
    /// A copy of the chart with the drafts in it, for checking them (<see cref="ReductionCheck"/>); nothing changes it, so it
    /// can be read off the editing thread.
    /// </summary>
    public EditableChart? Drafted { get; private set; }

    /// <summary>The targets that have notes now, which adding the drafts replaces.</summary>
    public IReadOnlyList<ChartDifficulty> Overwrites =>
        Part is { } part && _session.Chart.PartOf(part) is { } whole
            ? [.. Drafts.Select(static d => d.Target.Difficulty).Where(d => whole[d].Notes.Count > 0)]
            : [];

    /// <summary>The draft for <paramref name="key"/>, or null when the run has none.</summary>
    public ReductionDraft? DraftFor(ChartTrackKey key) => Drafts.FirstOrDefault(d => d.Target == key);

    /// <summary>
    /// Starts a run of a 5-fret or GHL part, in place of any going on: a draft of <paramref name="to"/> from
    /// <paramref name="from"/> by <paramref name="rules"/>, over <paramref name="range"/> or the whole part.
    /// </summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool Start(ChartInstrument part, ChartDifficulty from, ChartDifficulty to, FretReductionRules rules, (long From, long To)? range = null) =>
        Start(part, range, [(from, to)], (chart, f, t) => Reducer.Reduce(chart, part, f, t, rules, range, _session.ReadOptions));

    /// <summary>Starts a run of the drums, in place of any going on.</summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool Start(ChartDifficulty from, ChartDifficulty to, DrumReductionRules rules, (long From, long To)? range = null)
    {
        var variant = _session.DrumsVariantShown;
        return Start(ChartInstrument.Drums, range, [(from, to)], (chart, f, t) => Reducer.Reduce(chart, f, t, rules, variant, range));
    }

    /// <summary>
    /// Starts a run of a 5-fret or GHL part over several difficulties, in place of any going on: each of
    /// <paramref name="targets"/>, by its rules, from the nearest difficulty above it with notes, as it will be.
    /// </summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool StartAll(ChartInstrument part, IReadOnlyDictionary<ChartDifficulty, FretReductionRules> targets, (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return Start(part, range, [.. targets.Keys.Select(static t => ((ChartDifficulty?)null, t))], (chart, f, t) => Reducer.Reduce(chart, part, f, t, targets[t], range, _session.ReadOptions));
    }

    /// <summary>Starts a run of the drums over several difficulties, in place of any going on.</summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool StartAll(IReadOnlyDictionary<ChartDifficulty, DrumReductionRules> targets, (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var variant = _session.DrumsVariantShown;
        return Start(ChartInstrument.Drums, range, [.. targets.Keys.Select(static t => ((ChartDifficulty?)null, t))], (chart, f, t) => Reducer.Reduce(chart, f, t, targets[t], variant, range));
    }

    /// <summary>
    /// The drafts' lint findings, suggested ratings and order, with the chart's read options; null with no run. What it
    /// needs is read now, and it's worked out off the calling thread.
    /// </summary>
    public Task<ReductionCheck?> CheckAsync()
    {
        if (Drafted is not { } drafted || Part is not { } part)
            return Task.FromResult<ReductionCheck?>(null);
        List<ChartDifficulty> targets = [.. Drafts.Select(static d => d.Target.Difficulty)];
        var options = _session.ReadOptions;
        return Task.Run<ReductionCheck?>(() => ReductionCheck.Of(drafted, part, targets, options));
    }

    /// <summary>
    /// How each draft differs from its source as the games read them (<see cref="ReductionDiff"/>), highest first, with the
    /// chart's read options; empty with no run. Outside the range each is compared with its target as charted. What it
    /// needs is read now, and it's worked out off the calling thread.
    /// </summary>
    public Task<IReadOnlyList<ReductionDiff>> DiffAsync()
    {
        if (Drafted is not { } drafted || Part is not { } part)
            return Task.FromResult<IReadOnlyList<ReductionDiff>>([]);
        var steps = Steps;
        var range = Range;
        var options = _session.ReadOptions;
        var before = range is null ? null : Drafts.ToDictionary(static d => d.Target.Difficulty, d => _session.GameTrack(d.Target));
        return Task.Run(() => ReductionDiff.Of(drafted, part, steps, range, options, before));
    }

    /// <summary>Each draft's source and target, highest first.</summary>
    public IReadOnlyList<(ChartDifficulty Source, ChartDifficulty Target)> Steps => [.. Drafts.Select(static d => (d.Source, d.Target.Difficulty))];

    /// <summary>Adds the drafts as one undoable step, each in place of what its target had, and ends the run.</summary>
    /// <returns>Whether they were added.</returns>
    public bool Accept()
    {
        if (!IsActive)
            return false;
        _accepting = true;
        bool added;
        try
        {
            added = _session.ExecuteAsOne(Drafts.Select(static draft => (Func<(IChartCommand?, string?)>)(() => (DifficultyGenerator.Replace(draft), null))));
        }
        finally
        {
            _accepting = false;
        }

        if (added)
            End();
        return added;
    }

    /// <summary>Ends the run, adding nothing.</summary>
    public void Cancel()
    {
        if (IsActive)
            End();
    }

    /// <summary>Ends the run, as the chart changed under it, unless the change is its own.</summary>
    internal void OnChanged()
    {
        if (!IsActive || _accepting)
            return;
        End();
        _session.Status = Dropped;
    }

    // Each step's target from its source, or from the nearest difficulty above it with notes (DifficultyGenerator.Draft).
    private bool Start(ChartInstrument part, (long From, long To)? range, IReadOnlyList<(ChartDifficulty? From, ChartDifficulty To)> steps, Func<EditableChart, ChartDifficulty, ChartDifficulty, ReductionResult> reduce)
    {
        return StartCore(part, range, steps, reduce, generator: null, options: null);
    }

    /// <summary>
    /// Starts a run with a learned generator (spec §4.1): each step is drafted by <paramref name="generator"/>, falling back
    /// to <paramref name="rules"/> per difficulty, with <see cref="GeneratorNotes"/> saying why or how it drafted (§3.7).
    /// </summary>
    /// <returns>Whether there was anything to reduce.</returns>
    public bool StartWithGenerator(
        ChartInstrument part,
        (long From, long To)? range,
        IReadOnlyList<(ChartDifficulty? From, ChartDifficulty To)> steps,
        Func<EditableChart, ChartDifficulty, ChartDifficulty, ReductionResult> rules,
        IDifficultyGenerator generator,
        GenerationOptions? options = null)
    {
        return StartCore(part, range, steps, rules, generator, options);
    }

    /// <summary>What the run's generator said about each step, by target difficulty: why it fell back to Rules, or how it drafted (§3.7).</summary>
    public IReadOnlyDictionary<ChartDifficulty, string> GeneratorNotes { get; private set; } =
        new Dictionary<ChartDifficulty, string>();

    private bool StartCore(
        ChartInstrument part,
        (long From, long To)? range,
        IReadOnlyList<(ChartDifficulty? From, ChartDifficulty To)> steps,
        Func<EditableChart, ChartDifficulty, ChartDifficulty, ReductionResult> rules,
        IDifficultyGenerator? generator,
        GenerationOptions? options)
    {
        _session.Tools.Cancel();
        Cancel();
        GeneratorNotes = new Dictionary<ChartDifficulty, string>();
        if (_session.Chart.PartOf(part) is null)
        {
            _session.Status = NoSourceNotes;
            return false;
        }

        GeneratedDifficulties generated;
        var notes = new Dictionary<ChartDifficulty, string>();
        try
        {
            generated = DifficultyGenerator.Draft(
                _session.Chart, part, steps,
                (chart, f, t) => DifficultyGenerator.ReduceWith(chart, part, f, t, generator, options, range, _session.ReadOptions, notes, rules),
                range);
        }
        catch (Exception e) when (EditorSession.IsRefusal(e))
        {
            _session.Status = e.Message;
            return false;
        }

        if (generated.Drafts.Count == 0)
        {
            _session.Status = NoSourceNotes;
            return false;
        }

        Part = part;
        Range = range;
        Drafts = generated.Drafts;
        Drafted = generated.Drafted;
        GeneratorNotes = notes;
        _session.Status = null;
        _session.RaiseState();
        return true;
    }

    private void End()
    {
        Part = null;
        Range = null;
        Drafts = [];
        Drafted = null;
        GeneratorNotes = new Dictionary<ChartDifficulty, string>();
        _session.RaiseState();
    }
}
