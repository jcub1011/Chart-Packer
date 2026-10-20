using ChartShared.Core.Charting.Difficulty;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>The strain model, its normalization and how attributes are combined (docs/difficulty-model.md §3 to §6).</summary>
[TestClass]
public sealed class DifficultyModelTests
{
    private static readonly DifficultyParameters P = DifficultyParameters.Default;
    private static readonly DifficultyModel Model = DifficultyModel.For(P);

    [TestMethod]
    public void AStrainSettlesWhereTheClosedFormSays()
    {
        var raw = new DifficultyModel(P, DifficultyReferences.Raw);
        var state = new DifficultyState(raw);
        foreach (var note in DifficultyReferences.StrumPattern(P, 20))
            state.Advance(note);

        double interval = 60_000 / P.StrumReferenceBpm / 4;
        double cost = 1000 / interval * Math.Pow(P.AlternateStrumMs / interval, P.AlternateStrumPower);
        double kept = Math.Pow(DifficultyParameters.Retained(P.StrumHalfLife), interval / 1000);
        Assert.AreEqual(cost / (1 - kept), state.Levels.Strum, 1e-6 * state.Levels.Strum);
    }

    [TestMethod]
    public void EachReferencePatternScoresAHundred()
    {
        Assert.AreEqual(100, MeanAfterNotes(DifficultyReferences.StrumPattern(P, 60), 30, l => l.Strum), 0.01);
        Assert.AreEqual(100, MeanAfterNotes(DifficultyReferences.NotePattern(P, 60), 30, l => l.Note), 0.01);
        Assert.AreEqual(100, MeanAfterNotes(DifficultyReferences.RhythmPattern(P, 60), 30, l => l.Rhythm), 0.01);
        Assert.AreEqual(100, MeanAfterNotes(DifficultyReferences.PatternPattern(P, 180), 60, l => l.Pattern), 0.01);
    }

    [TestMethod]
    public void TheEnduranceReferenceHeldIndefinitelyIsAHundredEndurance()
    {
        double seconds = P.EnduranceHalfLife * 15;
        Assert.AreEqual(100, MeanAfterNotes(DifficultyReferences.EndurancePattern(P, seconds + 30), seconds, l => l.Endurance), 0.01);
    }

    [TestMethod]
    public void OneAttributeAtItsReferenceCombinesAsTheDocumentSays()
    {
        Assert.AreEqual(73.6, Model.Combine(new DifficultyAttributes(100, 0, 0, 0, 0)), 0.05);
        Assert.AreEqual(77.1, Model.Combine(new DifficultyAttributes(0, 100, 0, 0, 0)), 0.05);
        Assert.AreEqual(0, Model.Combine(new DifficultyAttributes(0, 0, 100, 0, 0)), 1e-9);
        Assert.AreEqual(64.8, Model.Combine(new DifficultyAttributes(0, 0, 0, 100, 0)), 0.05);
        Assert.AreEqual(100, Model.Combine(new DifficultyAttributes(100, 100, 100, 100, 100)), 1e-9);
    }

    [TestMethod]
    public void ThePhysicalLoadFollowsTheHandsStrainsSquareRoot()
    {
        // The strum or note strain alone, combined, then square-rooted on the 0-to-100 scale.
        Assert.AreEqual(100 * Math.Sqrt(Math.Pow(0.25 / 0.55, 0.25)), Model.Load(100, 0), 1e-9);
        Assert.AreEqual(100 * Math.Sqrt(Math.Pow(0.30 / 0.55, 0.25)), Model.Load(0, 100), 1e-9);
        Assert.AreEqual(100, Model.Load(100, 100), 1e-9);
        Assert.AreEqual(50, Model.Load(25, 25), 1e-9);
    }

    [TestMethod]
    public void SteadyModerateStrummingBuildsEndurance()
    {
        // Strummed eighths at 185 BPM moving between two frets for three minutes: far from the speed limit, but tiring.
        var notes = Enumerable.Range(0, 1110).Select(i => new DifficultyNote(i * 60_000 / 185.0 / 2, i * 0.5, (byte)(i / 8 % 2 == 0 ? 2 : 4), NoteKind.Strum, 0)).ToList();
        var easy = Enumerable.Range(0, 360).Select(i => new DifficultyNote(i * 500.0, i, 1, NoteKind.Strum, 0)).ToList();

        var steady = DifficultyAnalyzer.Analyze(notes).ChartAttributes;

        Assert.IsGreaterThan(steady.Strum, steady.Endurance);
        Assert.IsGreaterThan(10, steady.Endurance);
        Assert.AreEqual(0, DifficultyAnalyzer.Analyze(easy).ChartAttributes.Endurance);
    }

    [TestMethod]
    public void ABurstAtTheStrumReferenceIsRankFiveOrSixAndHoldingItIsAnOverchart()
    {
        // Fifteen seconds at the speed limit is realistic; minutes of it is beyond the stamina limit.
        var burst = DifficultyAnalyzer.Analyze([.. DifficultyReferences.StrumPattern(P, 15)]);
        var held = DifficultyAnalyzer.Analyze([.. DifficultyReferences.StrumPattern(P, 240)]);

        Assert.IsTrue(burst.Rank is >= 5 and < RankCurve.FirstOverchart, $"{burst.Rank}");
        Assert.IsGreaterThanOrEqualTo(RankCurve.FirstOverchart, held.Rank);
        Assert.IsGreaterThan(100, held.ChartAttributes.Endurance);
    }

    [TestMethod]
    public void HoldingTheEnduranceReferenceIsHardButRealistic()
    {
        // The stamina limit for a whole song, with the strumming itself well under the speed limit: endurance at 100 and
        // little else combine to about 65 (§5.1), as pattern alone at its reference does: the hardest realistic rank.
        var held = DifficultyAnalyzer.Analyze([.. DifficultyReferences.EndurancePattern(P, 240)]);

        Assert.AreEqual(100, held.ChartAttributes.Endurance, 2);
        Assert.IsTrue(held.Rank is >= 5 and < RankCurve.FirstOverchart, $"{held.Rank}");
    }

    [TestMethod]
    public void SustainedFrettingAtTheReferenceBuildsMoreEnduranceThanStrumming()
    {
        double seconds = P.EnduranceHalfLife * 15;
        double fretting = MeanAfterNotes(DifficultyReferences.NotePattern(P, seconds + 30), seconds, l => l.Endurance);
        double strumming = MeanAfterNotes(DifficultyReferences.StrumPattern(P, seconds + 30), seconds, l => l.Endurance);

        // Note weighs more than strum in the physical load.
        Assert.IsGreaterThan(strumming, fretting);
    }

    [TestMethod]
    public void AnEasyStreamScoresItsOwnSteadyDifficulty()
    {
        // Strummed notes on two frets, one a window at 150 BPM: well under the endurance threshold, so every window peaks
        // alike.
        var notes = Enumerable.Range(0, 150).Select(i => new DifficultyNote(i * 400.0, i, (byte)(i % 2 == 0 ? 1 : 2), NoteKind.Strum, 0)).ToList();
        var profile = DifficultyAnalyzer.Analyze(notes);
        double steady = profile.Samples.Last(s => s.TimeMs == notes[100].TimeMs).Overall;

        Assert.AreEqual(steady, profile.Score(20_000, 50_000), 1e-6 * steady);
        Assert.AreEqual(0, profile.ChartAttributes.Endurance);
    }

    [TestMethod]
    public void AStretchShorterThanAWindowScoresItsPeak()
    {
        var notes = new List<DifficultyNote> { new(1000, 2, 1, NoteKind.Strum, 0), new(1150, 2.3, 4, NoteKind.Strum, 0), new(1300, 2.6, 1, NoteKind.Strum, 0) };
        var profile = DifficultyAnalyzer.Analyze(notes);

        double score = profile.Score(1100, 1200);

        Assert.AreEqual(profile.ValueAt(1150).Overall, score, 1e-9);
        Assert.IsGreaterThan(profile.ValueAt(1100).Overall, score);
    }

    [TestMethod]
    public void BeforeTheFirstNoteAndForNoNotesEverythingIsZero()
    {
        var profile = DifficultyAnalyzer.Analyze([new DifficultyNote(5000, 10, 1, NoteKind.Strum, 0), new DifficultyNote(5100, 10.2, 2, NoteKind.Strum, 0)]);

        Assert.AreEqual(default, profile.ValueAt(4000));
        Assert.AreEqual(0, DifficultyAnalyzer.Analyze([]).ChartScore);
        Assert.AreEqual(0, DifficultyAnalyzer.Analyze([]).Tier);
    }

    [TestMethod]
    public void TheEnvelopeHoldsAPeakForAWindow()
    {
        var notes = Enumerable.Range(0, 16).Select(i => new DifficultyNote(1000 + (i * 60.0), 2 + (i * 0.125), (byte)(1 << (i % 5)), NoteKind.Hopo, 0)).ToList();
        var profile = DifficultyAnalyzer.Analyze(notes);
        double last = notes[^1].TimeMs;

        Assert.AreEqual(profile.ValueAt(last).Overall, profile.EnvelopeAt(last + P.WindowMs - 1).Overall, 1e-9);
        Assert.IsGreaterThan(profile.ValueAt(last + 200).Overall, profile.EnvelopeAt(last + 200).Overall);
        Assert.AreEqual(DifficultyAttribute.Note, profile.DominantAt(last));
    }

    [TestMethod]
    public void TheContributorsAreTheHardestWindows()
    {
        // An easy minute with a hard burst in the middle.
        var notes = new List<DifficultyNote>();
        for (int i = 0; i < 120; i++)
            notes.Add(new DifficultyNote(i * 500.0, i, 1, NoteKind.Strum, 0));
        for (int i = 0; i < 80; i++)
            notes.Add(new DifficultyNote(60_000 + (i * 75.0), 120 + (i * 0.25), (byte)(1 << (i % 5)), NoteKind.Strum, 0));
        var profile = DifficultyAnalyzer.Analyze(notes);

        var contributors = profile.Contributors();

        Assert.IsNotEmpty(contributors);
        Assert.IsTrue(contributors.All(c => c.FromMs >= 60_000 && c.FromMs < 66_400), string.Join(", ", contributors));
    }

    private static double MeanAfterNotes(IEnumerable<DifficultyNote> notes, double fromSeconds, Func<DifficultyAttributes, double> pick)
    {
        var state = new DifficultyState(Model);
        double sum = 0;
        int count = 0;
        foreach (var note in notes)
        {
            state.Advance(note);
            if (note.TimeMs >= fromSeconds * 1000)
            {
                sum += pick(state.Levels);
                count++;
            }
        }

        return sum / count;
    }
}
