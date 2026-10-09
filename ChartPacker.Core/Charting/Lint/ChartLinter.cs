using System.Diagnostics;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Validation;
using Serilog;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>
/// Runs chart lint rules on a snapshot of a chart (charter-spec C16): all of them over the whole chart, or, after a
/// change, each over as much as its <see cref="IChartLintRule.Scope"/> says, keeping the findings of the last run for
/// the rest.
/// </summary>
/// <remarks>
/// <para>
/// It keeps nothing between calls, and its results are immutable, so it can run on any thread. It reads only the
/// <see cref="ChartLintInput"/> it's given, whose chart is a snapshot (<see cref="EditableChart.Clone"/>) the caller takes
/// when the work starts, so the editor can go on changing the chart (§11). Rules run one after another, and one that
/// throws fails the run; the caller keeps the last result.
/// </para>
/// <para>
/// After a change, <see cref="Relint"/> checks again:
/// </para>
/// <list type="bullet">
/// <item><b>Every rule, over the whole chart</b>, when the tempo map or the song's events changed (see <see cref="FullRelintChanges"/>).</item>
/// <item><b>Song rules</b>, over the whole chart, after every change.</item>
/// <item>
/// <b>Part rules</b>, on each part the change touched: a region of it, the part added or removed, or the drums when the
/// song's drum variant changed. Their findings in that part are replaced; a part the chart no longer has keeps none.
/// </item>
/// <item>
/// <b>Range rules</b>, over each region the change touched widened by the rule's reach (see
/// <see cref="LintRegions.Widen"/>). Their findings that overlap a widened region are replaced by what the rule finds
/// there now: from each region's run, the findings that overlap it and no region before it.
/// </item>
/// </list>
/// <para>
/// A finding without a chart location belongs to the song as a whole: it's kept only from a song rule, which is checked
/// again after every change, and dropped, with a warning in the log, from a part or range rule.
/// </para>
/// </remarks>
public sealed class ChartLinter
{
    /// <summary>
    /// The changes to the song as a whole after which every rule is checked again over the whole chart: the tempo map,
    /// which moves every tick in time (C16 item 1), and the song's events (sections, <c>[coda]</c>, <c>[end]</c>), which
    /// any rule may read and which change seldom. A part added or removed comes with its whole region, and a drum
    /// variant changed touches the whole drums part, so neither needs more.
    /// </summary>
    public const SongChanges FullRelintChanges = SongChanges.TempoMap | SongChanges.Events;

    private readonly IChartLintRule[] _rules;
    private readonly Func<EditableChart, ChartReadOptions, ChartDocument> _project;

    /// <summary>A linter of every rule there is, <see cref="ChartLintRules.All"/>.</summary>
    public ChartLinter()
        : this(ChartLintRules.All)
    {
    }

    /// <param name="rules">The rules to run, in the order they're run; no two with one ID.</param>
    public ChartLinter(IEnumerable<IChartLintRule> rules)
        : this(rules, Charting.GameView.Project)
    {
    }

    /// <param name="rules">The rules to run, in the order they're run; no two with one ID.</param>
    /// <param name="project">Works out the game view, for a test to count.</param>
    internal ChartLinter(IEnumerable<IChartLintRule> rules, Func<EditableChart, ChartReadOptions, ChartDocument> project)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(project);
        _rules = [.. rules];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            if (rule is null)
                throw new ArgumentException("The rules can't include null.", nameof(rules));
            if (rule.RuleId is null || !ids.Add(rule.RuleId))
                throw new ArgumentException($"Each rule needs its own ID, and {rule.RuleId ?? "null"} isn't.", nameof(rules));
            if (!Enum.IsDefined(rule.Scope))
                throw new ArgumentException($"{rule.RuleId}'s scope, {rule.Scope}, isn't one there is.", nameof(rules));
            if (rule.Reach < 0)
                throw new ArgumentException($"{rule.RuleId}'s reach can't be negative.", nameof(rules));
        }

        _project = project;
    }

    private static ILogger Logger => Log.ForContext(typeof(ChartLinter));

    /// <summary>The rules it runs, in order.</summary>
    public IReadOnlyList<IChartLintRule> Rules => _rules;

    /// <summary>Runs every rule over the whole chart.</summary>
    public ChartLintResult LintAll(ChartLintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        long start = Stopwatch.GetTimestamp();
        var run = new LintRun(input, _project);
        var byRule = new Dictionary<string, Finding[]>(_rules.Length, StringComparer.Ordinal);
        foreach (var rule in _rules)
            byRule[rule.RuleId] = LintWhole(rule, run);

        var result = new ChartLintResult(byRule);
        LogRun("Full", run, result, start);
        return result;
    }

    /// <summary>
    /// Checks again what <paramref name="change"/> could have changed the findings of, keeping the rest of
    /// <paramref name="previous"/> (see the remarks on <see cref="ChartLinter"/>). It gives the same findings as
    /// <see cref="LintAll"/> would, provided <paramref name="previous"/> was found in the chart as it was before the
    /// change, with the same song settings.
    /// </summary>
    /// <param name="previous">The last result, for the chart before the change.</param>
    /// <param name="input">The chart after the change, and the song's settings.</param>
    /// <param name="change">What the change touched: the <see cref="IChartCommand.Affected"/> of the commands since <paramref name="previous"/>, merged.</param>
    /// <returns>The new result; <paramref name="previous"/> itself when the change touched nothing.</returns>
    public ChartLintResult Relint(ChartLintResult previous, ChartLintInput input, ChartChange change)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(change);
        if (change.IsEmpty)
            return previous;
        if ((change.Song & FullRelintChanges) != 0)
        {
            Logger.Debug("The change touched the song's {SongChanges}, so every rule is checked again", (change.Song & FullRelintChanges).ToString());
            return LintAll(input);
        }

        long start = Stopwatch.GetTimestamp();
        var chart = input.Chart;
        IReadOnlyList<ChartLocation> changed = change.Regions;
        if ((change.Song & SongChanges.DrumsVariant) != 0)
            changed = ChartChange.Of(changed.Append(new ChartLocation(ChartInstrument.Drums, null, 0, long.MaxValue))).Regions;
        var parts = changed.Select(static r => r.Part).Distinct().ToArray();

        var run = new LintRun(input, _project);
        var widened = new Dictionary<int, LintRegions>();
        var byRule = new Dictionary<string, Finding[]>(_rules.Length, StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            if (!previous.TryGetFindings(rule.RuleId, out var old))
            {
                // Not run last time, by a linter of other rules: there's nothing to keep.
                byRule[rule.RuleId] = LintWhole(rule, run);
                continue;
            }

            byRule[rule.RuleId] = rule.Scope switch
            {
                LintScope.Range => RelintRegions(rule, run, old, Widened(rule.Reach)),
                LintScope.Part => RelintParts(rule, run, old, parts),
                _ => LintWhole(rule, run),
            };
        }

        var result = new ChartLintResult(byRule);
        LogRun("Incremental", run, result, start);
        return result;

        LintRegions Widened(int reach)
        {
            if (!widened.TryGetValue(reach, out var regions))
                widened[reach] = regions = LintRegions.Widen(chart, changed, reach);
            return regions;
        }
    }

    // The rule's findings over the whole chart.
    private static Finding[] LintWhole(IChartLintRule rule, LintRun run)
    {
        var found = new List<Finding>();
        int unplaced = 0;
        foreach (var finding in Check(rule, run, new ChartLintContext(run, null, null)))
        {
            if (finding.Location is null && rule.Scope != LintScope.Song)
                unplaced++;
            else
                found.Add(finding);
        }

        WarnUnplaced(rule, unplaced);
        found.Sort(FindingOrder.Comparer);
        return [.. found];
    }

    // The rule's findings with those in each of parts found again.
    private static Finding[] RelintParts(IChartLintRule rule, LintRun run, Finding[] old, ChartInstrument[] parts)
    {
        var kept = new List<Finding>(old.Length);
        foreach (var finding in old)
        {
            if (finding.Location is { } location && Array.IndexOf(parts, location.Part) < 0)
                kept.Add(finding);
        }

        var found = new List<Finding>();
        int unplaced = 0;
        foreach (var part in parts)
        {
            if (run.Input.Chart.PartOf(part) is null)
                continue;
            foreach (var finding in Check(rule, run, new ChartLintContext(run, part, null)))
            {
                if (finding.Location is not { } location)
                    unplaced++;
                else if (location.Part == part)
                    found.Add(finding);
            }
        }

        WarnUnplaced(rule, unplaced);
        found.Sort(FindingOrder.Comparer);
        return FindingOrder.Merge(kept, found);
    }

    // The rule's findings with those that overlap the regions found again, each from the run of the first region it
    // overlaps, so a finding two regions share is kept once.
    private static Finding[] RelintRegions(IChartLintRule rule, LintRun run, Finding[] old, LintRegions regions)
    {
        var kept = new List<Finding>(old.Length);
        foreach (var finding in old)
        {
            if (finding.Location is { } location && !regions.Overlaps(location))
                kept.Add(finding);
        }

        var found = new List<Finding>();
        int unplaced = 0;
        for (int i = 0; i < regions.Regions.Count; i++)
        {
            var region = regions.Regions[i];
            if (run.Input.Chart.PartOf(region.Part) is null)
                continue;
            foreach (var finding in Check(rule, run, new ChartLintContext(run, region.Part, region)))
            {
                if (finding.Location is not { } location)
                    unplaced++;
                else if (regions.FirstOverlapping(location) == i)
                    found.Add(finding);
            }
        }

        WarnUnplaced(rule, unplaced);
        found.Sort(FindingOrder.Comparer);
        return FindingOrder.Merge(kept, found);
    }

    private static IEnumerable<Finding> Check(IChartLintRule rule, LintRun run, ChartLintContext context)
    {
        run.Runs++;
        return rule.Check(context) ?? [];
    }

    private static void WarnUnplaced(IChartLintRule rule, int count)
    {
        if (count > 0)
            Logger.Warning("{RuleId}, a {Scope} rule, found {Count} findings without a chart location, which were dropped", rule.RuleId, rule.Scope.ToString(), count);
    }

    private void LogRun(string kind, LintRun run, ChartLintResult result, long start) =>
        Logger.Debug("{Kind} lint: {Rules} rules in {Runs} runs, {Findings} findings, game view {GameView}, in {ElapsedMs:F1} ms",
            kind, _rules.Length, run.Runs, result.Findings.Count, run.BuiltGameView ? "built" : "not needed", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
}

/// <summary>What one lint run shares between its rules: the input, and the game view, worked out at most once.</summary>
internal sealed class LintRun(ChartLintInput input, Func<EditableChart, ChartReadOptions, ChartDocument> project)
{
    private ChartDocument? _gameView;
    private TempoMap? _tempoMap;

    public ChartLintInput Input { get; } = input;

    /// <summary>How many times a rule has been run.</summary>
    public int Runs { get; set; }

    /// <summary>Whether a rule has asked for the game view.</summary>
    public bool BuiltGameView => _gameView is not null;

    /// <summary>The game view, worked out the first time it's asked for.</summary>
    public ChartDocument GameView => _gameView ??= project(Input.Chart, Input.Options);

    /// <summary>The chart's tempo map, made the first time it's asked for.</summary>
    public TempoMap TempoMap => _tempoMap ??= Input.Chart.TempoMap.ToTempoMap();

    /// <summary>The chart's vocal parts, read the first time they're asked for.</summary>
    public IReadOnlyList<(Layout.VocalTrack Track, VocalTrackContent Content)> VocalParts => _vocalParts ??= ReadVocalParts(Input.Chart);

    private IReadOnlyList<(Layout.VocalTrack Track, VocalTrackContent Content)>? _vocalParts;

    private static List<(Layout.VocalTrack, VocalTrackContent)> ReadVocalParts(EditableChart chart)
    {
        var parts = new List<(Layout.VocalTrack, VocalTrackContent)>();
        foreach (var track in Enum.GetValues<Layout.VocalTrack>())
        {
            int index = Layout.VocalTracks.IndexIn(chart, track);
            if (index < 0)
                continue;
            try
            {
                parts.Add((track, VocalTrackContent.Of(chart.Passthrough[index])));
            }
            catch (ChartFormatException)
            {
                // Kept as it is, and not checked.
            }
        }

        return parts;
    }
}
