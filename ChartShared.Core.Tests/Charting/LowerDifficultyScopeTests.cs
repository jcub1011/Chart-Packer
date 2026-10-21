using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charting;

/// <summary>Which lower difficulties a batch fills (issue #120): those picked, of the parts picked, skipping or replacing what's charted.</summary>
[TestClass]
public sealed class LowerDifficultyScopeTests
{
    private static readonly HashSet<ChartDifficulty> Lower = [ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy];

    [TestMethod]
    public void MissingFillsEachEmptyDifficultyBelowTheHighestCharted()
    {
        var scope = LowerDifficultyScope.Missing;

        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy], scope.TargetsOf(ChartInstrument.Guitar, Charted(ChartDifficulty.Expert)));
        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Easy], scope.TargetsOf(ChartInstrument.Guitar, Charted(ChartDifficulty.Expert, ChartDifficulty.Medium)));
        Assert.AreSequenceEqual([ChartDifficulty.Easy], scope.TargetsOf(ChartInstrument.Guitar, Charted(ChartDifficulty.Medium)));
    }

    [TestMethod]
    public void ReplaceTakesInChartedDifficultiesButNeverTheHighest()
    {
        var scope = new LowerDifficultyScope(Lower, null, Replace: true);

        Assert.AreSequenceEqual(
            [ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy],
            scope.TargetsOf(ChartInstrument.Drums, Charted(ChartDifficulty.Expert, ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy)));
        Assert.AreSequenceEqual([ChartDifficulty.Medium, ChartDifficulty.Easy], scope.TargetsOf(ChartInstrument.Drums, Charted(ChartDifficulty.Hard, ChartDifficulty.Easy)));
    }

    [TestMethod]
    public void OnlyTheDifficultiesPickedAreFilled()
    {
        var scope = new LowerDifficultyScope(new HashSet<ChartDifficulty> { ChartDifficulty.Medium }, null, Replace: false);

        Assert.AreSequenceEqual([ChartDifficulty.Medium], scope.TargetsOf(ChartInstrument.Bass, Charted(ChartDifficulty.Expert)));
    }

    [TestMethod]
    public void APartNotPickedIsNotFilled()
    {
        var scope = new LowerDifficultyScope(Lower, new HashSet<ChartInstrument> { ChartInstrument.Drums }, Replace: false);

        Assert.IsFalse(scope.Covers(ChartInstrument.Guitar));
        Assert.IsEmpty(scope.TargetsOf(ChartInstrument.Guitar, Charted(ChartDifficulty.Expert)));
        Assert.HasCount(3, scope.TargetsOf(ChartInstrument.Drums, Charted(ChartDifficulty.Expert)));
    }

    [TestMethod]
    public void APartWithOnlyEasyChartedHasNothingToFillEvenWithReplace()
    {
        var scope = new LowerDifficultyScope(Lower, null, Replace: true);

        Assert.IsEmpty(scope.TargetsOf(ChartInstrument.Guitar, Charted(ChartDifficulty.Easy)));
        Assert.IsEmpty(scope.TargetsOf(ChartInstrument.Guitar, Charted()));
    }

    [TestMethod]
    public void AChartsTargetsFollowTheScope()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Hard, [new EditableNote(0, 1, 0)]).Apply(chart);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(chart);

        var missing = DifficultyGenerator.MissingTargets(chart);
        var replaced = DifficultyGenerator.TargetsFor(chart, new LowerDifficultyScope(Lower, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, Replace: true));

        Assert.AreSequenceEqual([ChartDifficulty.Medium, ChartDifficulty.Easy], missing[ChartInstrument.Guitar]);
        Assert.HasCount(3, missing[ChartInstrument.Drums]);
        Assert.AreSequenceEqual([ChartInstrument.Guitar], [.. replaced.Keys]);
        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy], replaced[ChartInstrument.Guitar]);
    }

    private static HashSet<ChartDifficulty> Charted(params ChartDifficulty[] difficulties) => [.. difficulties];
}
