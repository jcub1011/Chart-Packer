using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring;

/// <summary>A run of <see cref="DifficultyGenerator"/> over a part: the drafts, highest first, and a copy of the chart with them in it.</summary>
/// <param name="Drafted">A copy of the chart with the drafts in it; nothing else changes it.</param>
public sealed record GeneratedDifficulties(EditableChart Drafted, IReadOnlyList<ReductionDraft> Drafts);

/// <summary>
/// Fills a part's lower difficulties from those above them (charter-spec C9 item 2), without an editing session: what
/// Reduce all does in the editor (<see cref="ReduceProposals"/>), and what the library's quick fix for CHR087 does.
/// </summary>
/// <remarks>
/// Each target is reduced from the nearest difficulty above it with notes, as it will be once the targets above it are
/// drafted, so Medium is made from Hard's draft. Only 5-fret, GHL and drum parts can be filled.
/// </remarks>
public static class DifficultyGenerator
{
    /// <summary>Whether a part of <paramref name="kind"/> can be filled: 5-fret, GHL and drums.</summary>
    public static bool CanGenerate(ChartInstrumentKind kind) => kind is ChartInstrumentKind.FiveFret or ChartInstrumentKind.SixFret or ChartInstrumentKind.Drums;

    /// <summary>
    /// How <paramref name="chart"/>'s drums are charted, read with <paramref name="options"/>, as the editor draws them
    /// (<see cref="EditorSession.DrumsVariantShown"/>): its own variant, or else as the games read it.
    /// </summary>
    public static DrumsVariant DrumsVariantOf(EditableChart chart, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(options);
        if (chart.DrumsVariant is { } variant)
            return variant;
        return GameView.IsFiveLaneDrums(chart, options) ? DrumsVariant.FiveLane
            : GameView.DiscoFlips(chart, options).IsProDrums ? DrumsVariant.Pro
            : DrumsVariant.FourLane;
    }

    /// <summary>The highest difficulty of <paramref name="part"/> with notes; null with none, or for a part that can't be filled.</summary>
    public static ChartDifficulty? HighestCharted(EditablePart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!CanGenerate(part.Kind))
            return null;
        for (int i = part.Tracks.Count - 1; i >= 0; i--)
        {
            if (part.Tracks[i].Notes.Count > 0)
                return part.Tracks[i].Difficulty;
        }

        return null;
    }

    /// <summary>The difficulties of <paramref name="part"/> below <paramref name="highest"/> with no notes, highest first.</summary>
    public static IReadOnlyList<ChartDifficulty> MissingBelow(EditablePart part, ChartDifficulty highest)
    {
        ArgumentNullException.ThrowIfNull(part);
        return [.. Enum.GetValues<ChartDifficulty>().Where(d => d < highest && part[d].Notes.Count == 0).OrderDescending()];
    }

    /// <summary>Each part of <paramref name="chart"/> with a difficulty to fill, and those difficulties, highest first.</summary>
    public static IReadOnlyDictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>> MissingTargets(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var missing = new Dictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>>();
        foreach (var part in chart.Parts)
        {
            if (HighestCharted(part) is { } highest && MissingBelow(part, highest) is { Count: > 0 } targets)
                missing[part.Instrument] = targets;
        }

        return missing;
    }

    /// <summary>
    /// Fills <paramref name="targets"/> of <paramref name="part"/> by <paramref name="rules"/>, each from the nearest
    /// difficulty above it with notes. Drums are reduced as <paramref name="variant"/>.
    /// </summary>
    /// <returns>The drafts; none when there's nothing to reduce.</returns>
    /// <exception cref="InvalidOperationException">A draft can't be added to the chart.</exception>
    /// <exception cref="ArgumentException">A draft can't be added to the chart.</exception>
    public static GeneratedDifficulties Generate(EditableChart chart, ChartInstrument part, IReadOnlyCollection<ChartDifficulty> targets, ReductionDefaults rules, DrumsVariant variant)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(rules);
        var kind = ChartInstruments.KindOf(part);
        if (!CanGenerate(kind))
            throw new ArgumentException("Only 5-fret, GHL and drum parts can be filled.", nameof(part));

        List<(ChartDifficulty? From, ChartDifficulty To)> steps = [.. targets.Select(static t => ((ChartDifficulty?)null, t))];
        return kind == ChartInstrumentKind.Drums
            ? Draft(chart, part, steps, (c, f, t) => Reducer.Reduce(c, f, t, rules.DrumsFor(t), variant))
            : Draft(chart, part, steps, (c, f, t) => Reducer.Reduce(c, part, f, t, rules.FretsFor(kind, t)));
    }

    /// <summary>
    /// The drafts of <paramref name="steps"/>, each target from its source, or, without one, from the nearest difficulty
    /// above it with notes, highest target first, each added to a copy of the chart before the next is reduced. A target
    /// with nothing to reduce from is left out.
    /// </summary>
    /// <param name="range">The ticks reduced, both ends in, or null for the whole part.</param>
    /// <exception cref="InvalidOperationException">A draft can't be added to the chart.</exception>
    /// <exception cref="ArgumentException">A draft can't be added to the chart.</exception>
    public static GeneratedDifficulties Draft(
        EditableChart chart,
        ChartInstrument part,
        IReadOnlyList<(ChartDifficulty? From, ChartDifficulty To)> steps,
        Func<EditableChart, ChartDifficulty, ChartDifficulty, ReductionResult> reduce,
        (long From, long To)? range = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(reduce);
        var drafted = chart.Clone();
        var drafts = new List<ReductionDraft>();
        if (drafted.PartOf(part) is null)
            return new GeneratedDifficulties(drafted, drafts);

        foreach (var (given, to) in steps.OrderByDescending(static s => s.To))
        {
            var whole = drafted.PartOf(part)!;
            var from = given ?? Enum.GetValues<ChartDifficulty>().Where(d => d > to && HasNotes(whole[d], range)).Order().Cast<ChartDifficulty?>().FirstOrDefault();
            if (from is not { } source || !HasNotes(whole[source], range))
                continue;
            var draft = new ReductionDraft(source, new ChartTrackKey(part, to), reduce(drafted, source, to), whole[to]);
            Replace(draft).Apply(drafted);
            drafts.Add(draft);
        }

        return new GeneratedDifficulties(drafted, drafts);
    }

    /// <summary>The command that puts <paramref name="draft"/> in place of what its target has.</summary>
    public static ReplaceTrack Replace(ReductionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var (part, to) = draft.Target;
        bool drums = ChartInstruments.KindOf(part) == ChartInstrumentKind.Drums;
        return new ReplaceTrack(part, to, draft.Result.Notes, draft.Result.Flags, drums ? draft.Result.Mixes : null);
    }

    // Whether a source has notes to reduce: in the range, or anywhere for the whole part.
    private static bool HasNotes(EditableTrack track, (long From, long To)? range) =>
        range is { } r ? !track.Notes.Between(r.From, r.To).IsEmpty : track.Notes.Count > 0;
}
