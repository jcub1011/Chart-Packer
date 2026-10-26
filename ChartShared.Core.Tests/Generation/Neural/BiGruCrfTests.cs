using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class BiGruCrfTests
{
    private static BiGruCrf TinyModel(string[]? pairs = null, string[]? instruments = null)
    {
        var manifest = NeuralTestPacks.Manifest(pairs: pairs, instruments: instruments);
        return new BiGruCrf(NeuralTestPacks.Pack(manifest, NeuralTestPacks.RandomWeights(manifest, 4)));
    }

    private static List<BeatFrame> Beats(int count) =>
        [.. Enumerable.Range(0, count).Select(i => NeuralTestPacks.Beat(i * 480L, [0, 24], "k" + i))];

    private static readonly RunConditioning ExpertToEasy = new(ChartDifficulty.Expert, ChartDifficulty.Easy, ChartInstrument.Guitar, 0, 3);

    [TestMethod]
    public void EmissionsAreDeterministic()
    {
        var model = TinyModel();
        var beats = Beats(8);
        var sets = beats.Select(_ => new HashSet<int> { 0, 1, 2, 3 }).ToList();

        var first = model.Emissions(beats, ExpertToEasy, sets);
        var second = model.Emissions(beats, ExpertToEasy, sets);

        for (int i = 0; i < beats.Count; i++)
            for (int v = 0; v < 4; v++)
                Assert.AreEqual(first[i, v], second[i, v], "Same input and pack always give the same draft (§3.3).");
    }

    [TestMethod]
    public void MaskedCandidatesScoreNegativeInfinity()
    {
        var model = TinyModel();
        List<HashSet<int>> sets = [[1], [0, 1, 2, 3]];

        var emissions = model.Emissions(Beats(2), ExpertToEasy, sets);

        Assert.IsTrue(float.IsNegativeInfinity(emissions[0, 0]));
        Assert.IsFalse(float.IsNegativeInfinity(emissions[0, 1]));
        Assert.IsFalse(float.IsNegativeInfinity(emissions[1, 2]));
    }

    [TestMethod]
    public void AnyLengthWorksFromOneMeasureToASong()
    {
        var model = TinyModel();

        foreach (int count in new[] { 1, 4, 600 })
        {
            var beats = Beats(count);
            var emissions = model.Emissions(beats, ExpertToEasy, beats.Select(_ => new HashSet<int> { 0, 1 }).ToList());

            Assert.AreEqual(count, emissions.GetLength(0));
        }
    }

    [TestMethod]
    public void TheSourceDifficultyAndPartChangeTheScores()
    {
        // Spec §3.3: conditioning is the target, the source and the instrument, so Hard→Medium isn't Expert→Medium.
        var model = TinyModel(pairs: ["Expert->Medium", "Hard->Medium"], instruments: ["Guitar", "Bass"]);
        var beats = Beats(4);
        var fromExpert = model.Emissions(beats, new RunConditioning(ChartDifficulty.Expert, ChartDifficulty.Medium, ChartInstrument.Guitar, 0, 3));
        var fromHard = model.Emissions(beats, new RunConditioning(ChartDifficulty.Hard, ChartDifficulty.Medium, ChartInstrument.Guitar, 0, 3));
        var bass = model.Emissions(beats, new RunConditioning(ChartDifficulty.Expert, ChartDifficulty.Medium, ChartInstrument.Bass, 0, 3));

        Assert.AreNotEqual(fromExpert[0, 1], fromHard[0, 1]);
        Assert.AreNotEqual(fromExpert[0, 1], bass[0, 1]);
    }

    [TestMethod]
    public void BeatFramesAndPreparedScalarsScoreTheSame()
    {
        // The trainer feeds the scalars Prepare kept; the apps feed the beats. Both go through BeatFeatures.
        var model = TinyModel();
        var beats = Beats(5);

        var fromBeats = model.Emissions(beats, ExpertToEasy);
        var fromScalars = model.Emissions([.. beats.Select(static b => b.SourcePatternId)], [.. beats.Select(static b => b.Scalars)], 0, 0, 0, 3);

        for (int i = 0; i < beats.Count; i++)
            for (int v = 0; v < BeatPattern.Vocabulary.Length; v++)
                Assert.AreEqual(fromBeats[i, v], fromScalars[i, v]);
    }
}
