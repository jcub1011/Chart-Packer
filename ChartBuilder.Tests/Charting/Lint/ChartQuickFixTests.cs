using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;
using static ChartBuilder.Tests.Charting.Lint.LintCharts;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>Chart quick fixes (charter-spec C16 item 3), with a test strategy for a test rule.</summary>
[TestClass]
public sealed class ChartQuickFixTests
{
    [TestMethod]
    public void AChartFixIsOneCommandThatClearsItsFindingAndUndoingItBringsTheFindingBack()
    {
        var chart = Ladder(20);
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        var expert = guitar[ChartDifficulty.Expert];
        var held = expert.Notes[5];
        var stack = new ChartUndoStack(chart);
        stack.Execute(new SetSustain(guitar.Instrument, expert.Difficulty, [held], 1_440));
        var linter = new ChartLinter(TestLintRules.All);
        var before = linter.LintAll(Input(chart));
        var finding = before.FindingsOf(LongSustainRule.Id).Single();

        var fix = QuickFixRegistry.ProposeChartFix(finding, chart, ChartReadOptions.Default, [new ShortenSustainFix()]);
        Assert.IsNotNull(fix);
        stack.Execute(fix);
        var fixedNote = expert.Notes[5];
        var fixedResult = linter.Relint(before, Input(chart), fix.Affected);
        stack.Undo();
        var undone = linter.Relint(fixedResult, Input(chart), fix.Affected);

        Assert.AreEqual(held with { Length = 960 }, fixedNote);
        Assert.IsEmpty(fixedResult.FindingsOf(LongSustainRule.Id));
        AssertSameFindings(before, undone, "after undoing the fix");
    }

    [TestMethod]
    public void AFindingWhoseRuleHasNoChartFixStrategyGetsNoFix()
    {
        var chart = Ladder(4);
        var finding = new Finding(LongSustainRule.Id, Severity.Info, "Held.",
            Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 0, 0));

        Assert.IsNull(QuickFixRegistry.ProposeChartFix(finding, chart, ChartReadOptions.Default));
        Assert.IsNull(QuickFixRegistry.ProposeChartFix(finding, chart, ChartReadOptions.Default, []));
        Assert.IsNull(QuickFixRegistry.ProposeChartFix(finding with { RuleId = CloseRepeatRule.Id }, chart, ChartReadOptions.Default, [new ShortenSustainFix()]));
    }

    [TestMethod]
    public void ChartFixesAreKeptApartFromMetadataFixes()
    {
        Assert.IsTrue(QuickFixRegistry.ChartStrategies.All(s => s.RuleId.StartsWith("CHR", StringComparison.Ordinal)));
        Assert.AreEqual(QuickFixRegistry.ChartStrategies.Count, QuickFixRegistry.ChartStrategies.Select(s => s.RuleId).Distinct().Count(), "At most one per rule.");
        Assert.IsFalse(QuickFixRegistry.ChartStrategies.Any(s => QuickFixRegistry.IsSupported(s.RuleId)));
        Assert.IsFalse(QuickFixRegistry.IsSupported(LongSustainRule.Id));
        Assert.IsTrue(QuickFixRegistry.Strategies.All(s => s.RuleId.StartsWith("SNG", StringComparison.Ordinal)));
    }

    /// <summary>A test fix for <see cref="LongSustainRule"/>: the notes it found are held 960 ticks.</summary>
    private sealed class ShortenSustainFix : IChartQuickFixStrategy
    {
        public string RuleId => LongSustainRule.Id;

        public IChartCommand? Propose(Finding finding, EditableChart chart, ChartReadOptions options)
        {
            if (finding.Location is not { Difficulty: { } difficulty } location || chart.PartOf(location.Part) is not { } part)
                return null;
            var notes = part[difficulty].Notes.At(location.Tick).ToArray().Where(n => n.Length > 960).ToList();
            return notes.Count > 0 ? new SetSustain(location.Part, difficulty, notes, 960) : null;
        }
    }
}
