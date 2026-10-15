using System.Diagnostics;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using static ChartBuilder.Tests.Charting.Lint.LintCharts;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>The chart lint engine (charter-spec C16): rule scopes, whole and incremental runs, with the test rules.</summary>
[TestClass]
public sealed class ChartLinterTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const int Steps = 150;

    public TestContext TestContext { get; set; } = null!;

    public static IEnumerable<object[]> FixturesAndSeeds =>
        MidiFixtures.All.SelectMany(f => new[] { new object[] { f.Name, 1 }, [f.Name, 2] });

    // Incremental runs agree with whole ones.

    [TestMethod]
    [DynamicData(nameof(FixturesAndSeeds))]
    public void RelintingAfterEachRandomEditOfAFixtureFindsWhatLintingItAllFinds(string name, int seed)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;

        CheckRandomEdits(chart, fixture.Options, new Random((MidiFixtures.All.ToList().FindIndex(f => f.Name == name) * 100) + seed));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void RelintingAfterEachRandomEditOfAGeneratedChartFindsWhatLintingItAllFinds(int seed)
    {
        var random = new Random(seed);

        CheckRandomEdits(Generated(random, notes: 120), ChartReadOptions.Default, random);
    }

    [TestMethod]
    [DynamicData(nameof(FixturesAndSeeds))]
    public void RelintingAfterEachRandomEditWithTheRuleCatalogueFindsWhatLintingItAllFinds(string name, int seed)
    {
        // Each CHR rule keeps to its scope's contract: a range rule's findings depend only on what's within their
        // location and its reach, and a part rule's only on their part.
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;

        CheckRandomEdits(chart, fixture.Options, new Random((MidiFixtures.All.ToList().FindIndex(f => f.Name == name) * 100) + seed + 500), ChartLintRules.All);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RelintingAfterEachRandomEditOfAGeneratedChartWithTheRuleCatalogueFindsWhatLintingItAllFinds(int seed)
    {
        var random = new Random(seed + 50);

        CheckRandomEdits(Generated(random, notes: 120), ChartReadOptions.Default, random, ChartLintRules.All);
    }

    // Scopes.

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ATempoMapOrSongEventsChangeChecksEveryRuleOverTheWholeChart(bool tempo)
    {
        var chart = Ladder(40);
        var rules = TestLintRules.All.Select(r => new RecordingRule(r)).ToList();
        var linter = new ChartLinter(rules);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));
        rules.ForEach(r => r.Runs.Clear());

        stack.Execute(tempo ? new AddTempo(new TempoChange(1920, 400_000)) : new AddTextEvent(null, TextEvent.Of(960, "[section Verse]")));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        foreach (var rule in rules)
            Assert.AreSequenceEqual([(null, null)], rule.Runs, rule.RuleId);
    }

    [TestMethod]
    public void PartRulesAreCheckedAgainOnlyOnThePartsTheChangeTouched()
    {
        var chart = Generated(new Random(5), notes: 50);
        var part = new RecordingRule(new MissingDifficultyRule());
        var song = new RecordingRule(new NoSectionsRule());
        var linter = new ChartLinter([part, song]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));
        part.Runs.Clear();
        song.Runs.Clear();

        stack.Execute(new AddNotes(ChartInstrument.Bass, ChartDifficulty.Hard, [new EditableNote(15_390, 1, 2)]));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        Assert.AreSequenceEqual([(ChartInstrument.Bass, null)], part.Runs);
        Assert.AreSequenceEqual([(null, null)], song.Runs, "Song rules are checked over the whole chart after every change.");
    }

    [TestMethod]
    public void RangeRulesAreCheckedAgainOnlyOverTheChangedTicksWidenedByTheirReach()
    {
        // Expert has a chord every 480 ticks; the new note goes between 4800 and 5280.
        var chart = Ladder(40);
        var reachOne = new RecordingRule(new CloseRepeatRule());
        var reachZero = new RecordingRule(new LongSustainRule());
        var linter = new ChartLinter([reachOne, reachZero]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));
        reachOne.Runs.Clear();
        reachOne.Chords.Clear();
        reachZero.Runs.Clear();
        reachZero.Chords.Clear();

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(5_040, 1, 0)]));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        Assert.AreSequenceEqual([(Guitar, new ChartLocation(Guitar, Expert, 4_800, 5_280))], reachOne.Runs);
        Assert.AreSequenceEqual([(Guitar, Expert, 4_800L), (Guitar, Expert, 5_040L), (Guitar, Expert, 5_280L)], reachOne.Chords);
        Assert.AreSequenceEqual([(Guitar, new ChartLocation(Guitar, Expert, 5_040, 5_041))], reachZero.Runs);
        Assert.AreSequenceEqual([(Guitar, Expert, 5_040L)], reachZero.Chords);
    }

    [TestMethod]
    public void ARegionEveryDifficultySharesIsWidenedOnEveryDifficulty()
    {
        // Expert has a chord every 480 ticks, Hard every 960.
        var chart = Ladder(40);
        var reachOne = new RecordingRule(new CloseRepeatRule());
        var linter = new ChartLinter([reachOne]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));
        reachOne.Runs.Clear();

        stack.Execute(new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 5_000, 100)));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        // Back to 4800 on both; forward to 5280 on Expert and 5760 on Hard.
        Assert.AreSequenceEqual([(Guitar, new ChartLocation(Guitar, null, 4_800, 5_760))], reachOne.Runs);
    }

    [TestMethod]
    public void ARegionWithFewerChordsAroundItThanTheReachIsWidenedToThoseThereAre()
    {
        var chart = Ladder(3);
        var rule = new RecordingRule(new DelegateRule("TST900", LintScope.Range, _ => [], reach: 5));
        var linter = new ChartLinter([rule]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));
        rule.Runs.Clear();

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(600, 1, 0)]));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        Assert.AreSequenceEqual([(Guitar, new ChartLocation(Guitar, Expert, 0, 960))], rule.Runs);
    }

    [TestMethod]
    public void AnEmptyChangeGivesThePreviousResult()
    {
        var chart = Generated(new Random(6), notes: 50);
        var linter = new ChartLinter(TestLintRules.All);
        var previous = linter.LintAll(Input(chart));

        Assert.AreSame(previous, linter.Relint(previous, Input(chart), ChartChange.None));
    }

    [TestMethod]
    public void RemovingAPartDropsItsFindingsAndUndoingItFindsThemAgain()
    {
        var chart = Generated(new Random(7), notes: 80);
        var linter = new ChartLinter(TestLintRules.All);
        var stack = new ChartUndoStack(chart);
        var start = linter.LintAll(Input(chart));
        Assert.Contains(f => f.Location?.Part == ChartInstrument.Bass, start.Findings, "The chart should have findings on bass.");

        stack.Execute(new RemovePart(ChartInstrument.Bass));
        var removed = linter.Relint(start, Input(chart), stack.NextUndo!.Affected);
        stack.Undo();
        var restored = linter.Relint(removed, Input(chart), stack.NextRedo!.Affected);

        Assert.DoesNotContain(f => f.Location?.Part == ChartInstrument.Bass, removed.Findings);
        AssertSameFindings(start, restored, "after undoing the removal");
    }

    // What's kept from a run.

    [TestMethod]
    public void FindingsARangeRuleReturnsOutsideTheRegionItWasGivenAreNotKeptFromThatRun()
    {
        // A rule that finds every Expert note however little it's given, saying which run found it.
        var chart = Ladder(20);
        int run = 0;
        var rule = new DelegateRule("TST901", LintScope.Range, context =>
        {
            run++;
            return [.. context.Chart.PartOf(Guitar)![Expert].Notes.Select(n =>
                new Finding("TST901", Severity.Warning, $"Run {run}", Location: new ChartLocation(Guitar, Expert, n.Tick, n.Tick)))];
        });
        var linter = new ChartLinter([rule]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));

        stack.Execute(new SetSustain(Guitar, Expert, [new EditableNote(2_400, 1, 0)], 120));
        var result = linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        Assert.HasCount(20, result.Findings);
        Assert.AreSequenceEqual(["Run 2"], result.Findings.Where(f => f.Location!.Tick == 2_400).Select(f => f.Message).ToList());
        Assert.IsTrue(result.Findings.Where(f => f.Location!.Tick != 2_400).All(f => f.Message == "Run 1"));
    }

    [TestMethod]
    public void AFindingTwoChangedRegionsShareIsKeptOnce()
    {
        // The rule's finding spans the whole part, so both regions' runs return it.
        var chart = Ladder(20);
        var rule = new DelegateRule("TST902", LintScope.Range, _ =>
            [new Finding("TST902", Severity.Warning, "Everywhere", Location: new ChartLocation(Guitar, null, 0, long.MaxValue))]);
        var linter = new ChartLinter([rule]);
        var stack = new ChartUndoStack(chart);
        var previous = linter.LintAll(Input(chart));

        stack.Execute(new CompositeCommand(
        [
            new AddNotes(Guitar, Expert, [new EditableNote(60, 1, 0)]),
            new AddNotes(Guitar, ChartDifficulty.Hard, [new EditableNote(7_000, 1, 0)]),
        ]));
        var result = linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);

        Assert.HasCount(1, result.Findings);
    }

    [TestMethod]
    public void FindingsWithoutALocationAreKeptOnlyFromSongRules()
    {
        var chart = Ladder(20);
        Finding Unplaced(string id) => new(id, Severity.Warning, "About the song.");
        var linter = new ChartLinter(
        [
            new DelegateRule("TST910", LintScope.Range, _ => [Unplaced("TST910")]),
            new DelegateRule("TST911", LintScope.Part, _ => [Unplaced("TST911")]),
            new DelegateRule("TST912", LintScope.Song, _ => [Unplaced("TST912")]),
        ]);
        var stack = new ChartUndoStack(chart);
        var all = linter.LintAll(Input(chart));

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(60, 1, 0)]));
        var relinted = linter.Relint(all, Input(chart), stack.NextUndo!.Affected);

        Assert.AreSequenceEqual([Unplaced("TST912")], all.Findings.ToList());
        Assert.AreSequenceEqual([Unplaced("TST912")], relinted.Findings.ToList());
    }

    [TestMethod]
    public void FindingsAreInOrderOfPartDifficultyTickAndRule()
    {
        var chart = Generated(new Random(8), notes: 120);
        var result = new ChartLinter(TestLintRules.All).LintAll(Input(chart));
        Assert.IsGreaterThan(20, result.Findings.Count);

        var expected = result.Findings
            .OrderBy(f => f.Location is null ? 0 : 1)
            .ThenBy(f => f.Location?.Part)
            .ThenBy(f => f.Location?.Difficulty is { } d ? (int)d : -1)
            .ThenBy(f => f.Location?.Tick)
            .ThenBy(f => f.Location?.EndTick)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();
        Assert.AreSequenceEqual(expected, [.. result.Findings]);
    }

    // The game view.

    [TestMethod]
    public void TheGameViewIsWorkedOutAtMostOnceARunAndOnlyWhenARuleAsksForIt()
    {
        var chart = Generated(new Random(9), notes: 80);
        int built = 0;
        ChartDocument Project(EditableChart c, ChartReadOptions o)
        {
            built++;
            return GameView.Project(c, o);
        }

        var seen = new List<ChartDocument>();
        var reader = new DelegateRule("TST920", LintScope.Part, context =>
        {
            seen.Add(context.GameView);
            return [];
        });
        var linter = new ChartLinter([new GameHopoRule(), reader, .. TestLintRules.WithoutGameView], Project);
        var stack = new ChartUndoStack(chart);

        var previous = linter.LintAll(Input(chart));
        Assert.AreEqual(1, built, "A whole run in which two rules read the game view.");

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(15_330, 1, 1)]));
        linter.Relint(previous, Input(chart), stack.NextUndo!.Affected);
        Assert.AreEqual(2, built, "An incremental run in which two rules read the game view.");
        Assert.HasCount(2, seen);

        var withoutGameView = new ChartLinter(TestLintRules.WithoutGameView, Project);
        var last = withoutGameView.LintAll(Input(chart));
        stack.Execute(new AddTempo(new TempoChange(960, 400_000)));
        withoutGameView.Relint(last, Input(chart), stack.NextUndo!.Affected);
        Assert.AreEqual(2, built, "Runs in which no rule reads the game view.");
    }

    // The linter.

    [TestMethod]
    public void TwoRulesWithOneIdAreRefused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ChartLinter([new LongSustainRule(), new LongSustainRule(480)]));
    }

    [TestMethod]
    public void TheDefaultLinterRunsTheRuleCatalogue()
    {
        var linter = new ChartLinter();

        Assert.IsNotEmpty(ChartLintRules.All);
        Assert.AreSequenceEqual(ChartLintRules.All.Select(r => r.RuleId).ToList(), [.. linter.Rules.Select(r => r.RuleId)]);
        Assert.IsTrue(linter.Rules.All(r => r.RuleId.StartsWith("CHR", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RelintingAResultOfOtherRulesChecksTheRulesItLacksOverTheWholeChart()
    {
        var chart = Generated(new Random(11), notes: 80);
        var previous = new ChartLinter([new LongSustainRule()]).LintAll(Input(chart));
        var linter = new ChartLinter(TestLintRules.All);
        var stack = new ChartUndoStack(chart);

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(15_330, 1, 1)]));
        var input = Input(chart);

        AssertSameFindings(linter.LintAll(input), linter.Relint(previous, input, stack.NextUndo!.Affected), "after relinting a result of one rule");
    }

    [TestMethod]
    public void LintAfterASingleNoteEditOnA20000NoteChartTakesUnder50Milliseconds()
    {
        // AC C16.2. Timed from taking the snapshot to the findings being ready, after a warm-up; the best of five runs,
        // so a busy machine doesn't fail it.
        var chart = Big();
        var linter = new ChartLinter([.. TestLintRules.Range, new MissingDifficultyRule(), new NoSectionsRule()]);
        var stack = new ChartUndoStack(chart);
        long start = Stopwatch.GetTimestamp();
        var result = linter.LintAll(Input(chart));
        var whole = Stopwatch.GetElapsedTime(start);

        var best = TimeSpan.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            // Between two chords, in the middle of the song.
            stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1_200_060 + (i * 120L), 1, i % 5)]));
            var change = stack.NextUndo!.Affected;
            start = Stopwatch.GetTimestamp();
            result = linter.Relint(result, Input(chart), change);
            _ = result.Findings.Count;
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (i > 0 && elapsed < best)
                best = elapsed;

            stack.Undo();
            result = linter.Relint(result, Input(chart), stack.NextRedo!.Affected);
        }

        TestContext.WriteLine($"AC C16.2: lint of 26,000 notes, whole {whole.TotalMilliseconds:F1} ms; after a single-note edit, best {best.TotalMilliseconds:F2} ms, {result.Findings.Count} findings.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(50), best);
        AssertSameFindings(linter.LintAll(Input(chart)), result, "after the timed edits");
    }

    [TestMethod]
    public void LintWithTheRuleCatalogueAfterASingleNoteEditOnA20000NoteChartTakesUnder50Milliseconds()
    {
        // AC C16.2, with the CHR rules.
        var chart = Big();
        var linter = new ChartLinter();
        var stack = new ChartUndoStack(chart);
        var input = CatalogueInput(chart, ChartReadOptions.Default);
        long start = Stopwatch.GetTimestamp();
        var result = linter.LintAll(input);
        var whole = Stopwatch.GetElapsedTime(start);

        var best = TimeSpan.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1_200_060 + (i * 120L), 1, i % 5)]));
            var change = stack.NextUndo!.Affected;
            start = Stopwatch.GetTimestamp();
            result = linter.Relint(result, CatalogueInput(chart, ChartReadOptions.Default), change);
            _ = result.Findings.Count;
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (i > 0 && elapsed < best)
                best = elapsed;

            stack.Undo();
            result = linter.Relint(result, CatalogueInput(chart, ChartReadOptions.Default), stack.NextRedo!.Affected);
        }

        TestContext.WriteLine($"AC C16.2, CHR rules: whole {whole.TotalMilliseconds:F1} ms; after a single-note edit, best {best.TotalMilliseconds:F2} ms, {result.Findings.Count} findings.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(50), best);
        AssertSameFindings(linter.LintAll(CatalogueInput(chart, ChartReadOptions.Default)), result, "after the timed edits");
    }

    /// <summary>
    /// Runs <see cref="Steps"/> random steps on <paramref name="chart"/> through the undo stack (commands of every kind,
    /// undo, redo, and now and then a few commands relinted as one change), relinting after each, and checks each result
    /// against linting the whole chart.
    /// </summary>
    private static void CheckRandomEdits(EditableChart chart, ChartReadOptions options, Random random, IReadOnlyList<IChartLintRule>? rules = null)
    {
        var linter = new ChartLinter(rules ?? TestLintRules.All);
        Func<EditableChart, ChartReadOptions, ChartLintInput> inputOf = rules is null ? (c, o) => Input(c, o) : CatalogueInput;
        var stack = new ChartUndoStack(chart);
        var commands = new CommandSequenceTests.RandomCommands(random);
        var result = linter.LintAll(inputOf(chart, options));
        var actions = new HashSet<string>();
        int relinted = 0;
        for (int step = 0; step < Steps; step++)
        {
            var change = ChartChange.None;
            string what;
            int roll = random.Next(20);
            if (roll < 2 && stack.CanUndo)
            {
                what = $"undoing {stack.NextUndo!.GetType().Name}";
                stack.Undo();
                change = stack.NextRedo!.Affected;
            }
            else if (roll < 8 && stack.CanRedo)
            {
                what = $"redoing {stack.NextRedo!.GetType().Name}";
                stack.Redo();
                change = stack.NextUndo!.Affected;
            }
            else
            {
                // Mostly one command; now and then a few, as when edits come faster than lint.
                int count = roll == 19 ? 3 : 1;
                var names = new List<string>();
                for (int attempt = 0; names.Count < count && attempt < 50; attempt++)
                {
                    if (commands.Next(chart) is not { } command)
                        continue;
                    try
                    {
                        stack.Execute(command);
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
                    {
                        continue;
                    }

                    names.Add(command.GetType().Name);
                    change = change.Merge(command.Affected);
                }

                what = string.Join(" and ", names);
            }

            actions.Add(what.Split(' ')[0]);
            var input = inputOf(chart, options);
            result = linter.Relint(result, input, change);
            AssertSameFindings(linter.LintAll(input), result, $"at step {step}, after {what} touching {change}");
            if (!change.TempoMapChanged && (change.Song & SongChanges.Events) == 0)
                relinted++;
        }

        Assert.IsGreaterThan(Steps / 2, relinted, "Most steps should be incremental runs.");
        Assert.IsTrue(actions.Contains("undoing") && actions.Contains("redoing"), string.Join(", ", actions));
    }

    /// <summary>A snapshot for the CHR rules, with an audio length and a song.ini for the ones that read them.</summary>
    private static ChartLintInput CatalogueInput(EditableChart chart, ChartReadOptions options) =>
        new(chart.Clone(), [KeyValuePair.Create("song_length", "60000"), KeyValuePair.Create("diff_guitar", "3")], options, TimeSpan.FromSeconds(100), TimeSpan.FromMilliseconds(30));
}
