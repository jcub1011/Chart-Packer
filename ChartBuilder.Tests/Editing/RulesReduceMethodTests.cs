using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Editing;

/// <summary>The Reduce dialog's default reduction method: which rules it shows and that Reset puts back the shipped ones.</summary>
[TestClass]
public sealed class RulesReduceMethodTests
{
    private static RulesReduceMethodViewModel Method() =>
        new(ReductionDefaults.Shipped, ChartInstrumentKind.FiveFret, ChartDifficulty.Hard);

    [TestMethod]
    public void ShowsTheTargetPicked()
    {
        var method = Method();

        method.ShowForTarget(ChartDifficulty.Medium);

        Assert.AreEqual(ChartDifficulty.Medium, method.Rules.Target);
    }

    [TestMethod]
    public void RulesForSwitchShowsThatTarget()
    {
        var method = Method();

        method.SelectedRulesFor = method.RulesForChoices.First(c => c.Difficulty == ChartDifficulty.Easy);

        Assert.AreEqual(ChartDifficulty.Easy, method.Rules.Target);
    }

    [TestMethod]
    public void ResetPutsBackTheShippedRules()
    {
        var method = Method();
        method.Rules.MaxChordSize = 5;

        method.Reset();

        Assert.AreEqual(2m, method.Rules.MaxChordSize);
    }

    [TestMethod]
    public void AChangedRuleRaisesChanged()
    {
        var method = Method();
        bool changed = false;
        method.Changed += (_, _) => changed = true;

        method.Rules.MaxChordSize = 5;

        Assert.IsTrue(changed);
    }
}
