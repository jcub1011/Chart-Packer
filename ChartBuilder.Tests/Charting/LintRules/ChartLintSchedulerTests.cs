using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartBuilder.Tests.Charting.LintRules;

/// <summary>Keeping an open chart linted, one run at a time, while it's edited (charter-spec C16 item 1).</summary>
[TestClass]
public sealed class ChartLintSchedulerTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    [TestMethod]
    public void EditsMadeWhileARunIsGoingAreLintedByTheNextAgainstItsResult()
    {
        var (chart, stack, linter, scheduler) = Setup();
        Finish(scheduler, chart);

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(247, 1, 2)]));
        scheduler.Changed(stack.NextUndo!.Affected);
        var job = scheduler.Start(Input(chart))!;

        // Edited again before the run finishes.
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(733, 1, 3)]));
        scheduler.Changed(stack.NextUndo!.Affected);
        Assert.IsNull(scheduler.Start(Input(chart)), "One run at a time.");
        scheduler.Finish(job, ChartLintScheduler.Run(job));
        Assert.IsTrue(scheduler.HasWork);
        Finish(scheduler, chart);

        Assert.IsFalse(scheduler.HasWork);
        Assert.AreSequenceEqual(
            linter.LintAll(Input(chart)).Findings.ToList(),
            [.. scheduler.Current.Findings]);
        Assert.HasCount(2, scheduler.Current.FindingsOf(ChrRules.Chr004));
    }

    [TestMethod]
    public void TheFirstRunAndOneAfterTheSettingsChangeAreFull()
    {
        var (chart, _, _, scheduler) = Setup();

        Assert.IsTrue(scheduler.HasWork);
        var first = scheduler.Start(Input(chart))!;
        Assert.IsTrue(first.IsFull);
        scheduler.Finish(first, ChartLintScheduler.Run(first));
        Assert.IsNull(scheduler.Start(Input(chart)), "Nothing changed since.");

        scheduler.SettingsChanged();
        Assert.IsTrue(scheduler.Start(Input(chart))!.IsFull);
    }

    [TestMethod]
    public void AFailedRunKeepsTheFindingsAndTheNextIsFull()
    {
        var (chart, stack, _, scheduler) = Setup();
        Finish(scheduler, chart);
        var kept = scheduler.Current;
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(247, 1, 2)]));
        scheduler.Changed(stack.NextUndo!.Affected);
        var job = scheduler.Start(Input(chart))!;

        scheduler.Finish(job, null);

        Assert.AreSame(kept, scheduler.Current);
        Assert.IsTrue(scheduler.Start(Input(chart))!.IsFull);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.Finish(job, null), "Not the run that's running.");
    }

    private static (EditableChart Chart, ChartUndoStack Stack, ChartLinter Linter, ChartLintScheduler Scheduler) Setup()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
        new AddNotes(Guitar, Expert, [.. Enumerable.Range(0, 8).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        var linter = new ChartLinter();
        return (chart, new ChartUndoStack(chart), linter, new ChartLintScheduler(linter));
    }

    private static void Finish(ChartLintScheduler scheduler, EditableChart chart)
    {
        var job = scheduler.Start(Input(chart))!;
        scheduler.Finish(job, ChartLintScheduler.Run(job));
    }

    private static ChartLintInput Input(EditableChart chart) => new(chart.Clone(), [KeyValuePair.Create("diff_guitar", "3")]);
}
