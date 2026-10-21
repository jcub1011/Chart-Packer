using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

/// <summary><c>reduction-rules.json</c>: the default reduction rules both apps read and write (charter-spec C9 item 2, §10).</summary>
[TestClass]
public sealed class ReductionRulesFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;
    private string _builder = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-reduction-rules-");
        _path = Path.Combine(_root.FullName, "reduction-rules.json");
        _builder = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void WithNoFileTheRulesAreTheShippedOnes()
    {
        var rules = new ReductionRulesFile(_path, _builder).Load();

        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretEasy, rules.FiveFretEasy);
        Assert.AreEqual(ReductionDefaults.Shipped.DrumsHard, rules.DrumsHard);
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void RulesSavedByOneAreReadByAnother()
    {
        var easy = ReductionDefaults.Shipped.FiveFretEasy with { MaxChordSize = 2 };
        Assert.IsTrue(new ReductionRulesFile(_path, null).Save(ReductionDefaults.Shipped.WithFrets(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy, easy)));

        var read = new ReductionRulesFile(_path, null).Load();

        Assert.AreEqual(easy, read.FiveFretEasy);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretHard, read.FiveFretHard);
    }

    [TestMethod]
    public void UntilItsSavedTheRulesAreChartBuildersOld()
    {
        // As an older Chart Builder saved them in its own settings, with a value out of range.
        File.WriteAllText(_builder, """{ "DefaultCharterName": "Me", "ReductionRules": { "DrumsEasy": { "PadsPerHit": 9, "ThinningDivision": 4, "KickDivision": 4 } } }""");
        var file = new ReductionRulesFile(_path, _builder);

        var migrated = file.Load();

        Assert.AreEqual(4, migrated.DrumsEasy.ThinningDivision);
        Assert.AreEqual(5, migrated.DrumsEasy.PadsPerHit);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretHard, migrated.FiveFretHard);

        // Once saved, the file has them, and Chart Builder's settings are left as they were.
        string builder = File.ReadAllText(_builder);
        Assert.IsTrue(file.Save(migrated));
        Assert.AreEqual(4, new ReductionRulesFile(_path, null).Load().DrumsEasy.ThinningDivision);
        Assert.AreEqual(builder, File.ReadAllText(_builder));
    }

    [TestMethod]
    public void ASetSavedBeforeBurstsThinningAsShippedTakesTheShippedBurstsAndHopos()
    {
        // Saved before the bursts were added, with Medium's old default of no HOPOs kept, and while thinning had a gap in
        // milliseconds, which it no longer has.
        File.WriteAllText(_path, """{ "FiveFretMedium": { "MaxChordSize": 2, "HighestLane": 3, "ThinningDivision": 8, "KeepForcedHopoAndTap": false, "MinGapMs": 200 } }""");

        var medium = new ReductionRulesFile(_path, null).Load().FiveFretMedium;

        var shipped = ReductionDefaults.Shipped.FiveFretMedium;
        Assert.AreEqual(8, medium.ThinningDivision);
        Assert.AreEqual(shipped.BurstDivision, medium.BurstDivision);
        Assert.AreEqual(shipped.MaxBurst, medium.MaxBurst);
        Assert.IsTrue(medium.KeepForcedHopoAndTap, "A burst's chords are played as the source's only with HOPOs kept.");
    }

    [TestMethod]
    public void ASetSavedBeforeBurstsWithItsOwnThinningTakesNone()
    {
        File.WriteAllText(_path, """{ "FiveFretHard": { "MaxChordSize": 2, "HighestLane": 4, "ThinningDivision": 4, "KeepForcedHopoAndTap": false } }""");

        var hard = new ReductionRulesFile(_path, null).Load().FiveFretHard;

        Assert.AreEqual(4, hard.ThinningDivision);
        Assert.IsNull(hard.BurstDivision);
        Assert.IsNull(hard.MaxBurst);
        Assert.IsFalse(hard.KeepForcedHopoAndTap);
    }

    [TestMethod]
    public void ASaveKeepsTheSetsAnotherProcessChanged()
    {
        var mine = new ReductionRulesFile(_path, null);
        var theirs = new ReductionRulesFile(_path, null);
        var rules = mine.Load();
        var other = theirs.Load();

        theirs.Save(other.WithDrums(ChartDifficulty.Hard, other.DrumsHard with { PadsPerHit = 1 }));
        mine.Save(rules.WithFrets(ChartInstrumentKind.SixFret, ChartDifficulty.Medium, rules.SixFretMedium with { MaxChordSize = 1 }));

        var read = new ReductionRulesFile(_path, null).Load();
        Assert.AreEqual(1, read.DrumsHard.PadsPerHit);
        Assert.AreEqual(1, read.SixFretMedium.MaxChordSize);
    }

    [TestMethod]
    public void AResetPutsBackTheSetsAnotherProcessChanged()
    {
        var mine = new ReductionRulesFile(_path, null);
        var theirs = new ReductionRulesFile(_path, null);
        mine.Load();
        var other = theirs.Load();
        theirs.Save(other.WithFrets(ChartInstrumentKind.FiveFret, ChartDifficulty.Hard, other.FiveFretHard with { MaxChordSize = 1 }));

        Assert.IsTrue(mine.Reset());

        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretHard, new ReductionRulesFile(_path, null).Load().FiveFretHard);
    }
}
