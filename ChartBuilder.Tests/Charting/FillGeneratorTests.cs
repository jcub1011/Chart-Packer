using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// Generate fills (charter-spec C6 item 5): where <see cref="FillGenerator"/> proposes fills, and where it leaves them
/// out, at 480 ticks per beat in 4/4, so a measure is 1,920 ticks.
/// </summary>
[TestClass]
public sealed class FillGeneratorTests
{
    private const long Measure = 1920;
    private const int Kick = 0, Red = 1, Blue = 3, Green = 4, FifthPad = 5;

    // Star power over measures 0 and 1, so fills can start from measure 1's second half on.
    private static readonly Phrase FirstStarPower = new(PhraseKind.StarPower, 0, 960);
    private static readonly Phrase SecondStarPower = new(PhraseKind.StarPower, Measure, 960);

    [TestMethod]
    public void FillsStartEverySpacingMeasuresAndEndTheTickAfterTheCrashOnTheNextMeasuresFirstBeat()
    {
        var chart = Chart(DrumsVariant.Pro);

        var generated = Propose(chart, DrumsVariant.Pro);

        Assert.AreEqual(NoFillsReason.None, generated.Reason);
        Assert.AreSequenceEqual([2L, 6L, 10L, 14L, 18L], StartMeasures(generated));
        foreach (var proposal in generated.Proposals)
        {
            // CHR043's fix's convention: the fill's last tick is its activation note's.
            Assert.AreEqual(PhraseKind.DrumFill, proposal.Fill.Kind);
            Assert.AreEqual(proposal.Fill.Tick + Measure, proposal.ActivationTick);
            Assert.AreEqual(proposal.ActivationTick + 1, proposal.Fill.CoverEnd);
        }
    }

    [TestMethod]
    public void NothingIsProposedBeforeTheSecondStarPowerPhraseEnds()
    {
        var chart = Chart(DrumsVariant.Pro, starPower: [FirstStarPower, new Phrase(PhraseKind.StarPower, Measure, 2500)]);

        var generated = Propose(chart, DrumsVariant.Pro);

        // It ends at 4420, in measure 2, so measure 3 is the first line after it.
        Assert.AreEqual(3, StartMeasures(generated)[0]);
        Assert.IsTrue(generated.Proposals.All(p => p.Fill.Tick >= Measure + 2500));
    }

    [TestMethod]
    public void AFillStartsOnTheMeasureLineTheSecondStarPowerPhraseEndsOn()
    {
        // It covers measures 1 and 2, up to the tick before measure 3's line.
        var chart = Chart(DrumsVariant.Pro, starPower: [FirstStarPower, new Phrase(PhraseKind.StarPower, Measure, 2 * Measure)]);

        var generated = Propose(chart, DrumsVariant.Pro);

        Assert.AreEqual(3 * Measure, generated.Proposals[0].Fill.Tick);
        Assert.AreSequenceEqual([3L, 7L, 11L, 15L, 19L], StartMeasures(generated));
    }

    [TestMethod]
    public void AFillBeforeTheEarliestPointStillSpacesTheProposals()
    {
        // Star power until measure 3's second half, so fills could start from measure 4; the fill at measure 2 holds them
        // off until measure 6.
        var chart = Chart(DrumsVariant.Pro, starPower: [FirstStarPower, new Phrase(PhraseKind.StarPower, 3 * Measure, 960)]);
        Assert.AreEqual(4L, StartMeasures(Propose(chart, DrumsVariant.Pro))[0]);
        chart.PartOf(ChartInstrument.Drums)!.Phrases.Add(new Phrase(PhraseKind.DrumFill, 2 * Measure, Measure + 1));

        Assert.AreSequenceEqual([6L, 10L, 14L, 18L], StartMeasures(Propose(chart, DrumsVariant.Pro)));
    }

    [TestMethod]
    public void NothingIsProposedWithFewerThanTwoStarPowerPhrases()
    {
        var one = Chart(DrumsVariant.Pro, starPower: [FirstStarPower]);
        var overlapping = Chart(DrumsVariant.Pro, starPower: [new Phrase(PhraseKind.StarPower, 0, 960), new Phrase(PhraseKind.StarPower, 480, 960)]);

        foreach (var chart in new[] { one, overlapping })
        {
            var generated = Propose(chart, DrumsVariant.Pro);
            Assert.IsEmpty(generated.Proposals);
            Assert.AreEqual(NoFillsReason.TooFewStarPower, generated.Reason);
        }
    }

    [TestMethod]
    public void NothingIsProposedForDrumsWithoutNotes()
    {
        var generated = Propose(Drums(), DrumsVariant.Pro);

        Assert.IsEmpty(generated.Proposals);
        Assert.AreEqual(NoFillsReason.NoDrumNotes, generated.Reason);
    }

    [TestMethod]
    public void AFillNeverOverlapsStarPower()
    {
        // A third phrase over measure 10: neither measure 9's fill, whose activation note is its start, nor measure 10's fits.
        var third = new Phrase(PhraseKind.StarPower, 10 * Measure, Measure);
        var chart = Chart(DrumsVariant.Pro, starPower: [FirstStarPower, SecondStarPower, third]);

        var generated = Propose(chart, DrumsVariant.Pro);

        Assert.AreSequenceEqual([2L, 6L, 11L, 15L, 19L], StartMeasures(generated));
        Assert.DoesNotContain(p => p.Fill.Overlaps(third), generated.Proposals);
    }

    [TestMethod]
    public void NoFillIsProposedAtOrAfterCodaNorEndsThere()
    {
        var chart = Chart(DrumsVariant.Pro);
        new AddTextEvent(null, TextEvent.Of(11 * Measure, "[coda]")).Apply(chart);

        var generated = Propose(chart, DrumsVariant.Pro);

        // Measure 10's fill would end on [coda]'s tick.
        Assert.AreSequenceEqual([2L, 6L], StartMeasures(generated));
    }

    [TestMethod]
    public void OnProDrumsACrashIsAGreenCymbalNotAGreenTom()
    {
        var chart = Chart(DrumsVariant.Pro);
        chart.PartOf(ChartInstrument.Drums)!.TomMarkers.Add(new TomMarker(Green, 3 * Measure, 1));

        Assert.AreSequenceEqual([3L, 7L, 11L, 15L, 19L], StartMeasures(Propose(chart, DrumsVariant.Pro)));

        // A blue cymbal isn't a crash.
        Assert.AreEqual(NoFillsReason.NoSpotFits, Propose(Chart(DrumsVariant.Pro, crashPad: Blue), DrumsVariant.Pro).Reason);
    }

    [TestMethod]
    public void OnFourLaneDrumsACrashIsAnyGreen()
    {
        var chart = Chart(DrumsVariant.FourLane);
        chart.PartOf(ChartInstrument.Drums)!.TomMarkers.Add(new TomMarker(Green, 3 * Measure, 1));

        Assert.AreSequenceEqual([2L, 6L, 10L, 14L, 18L], StartMeasures(Propose(chart, DrumsVariant.FourLane)));
    }

    [TestMethod]
    public void OnFiveLaneDrumsACrashIsOrangeNotGreen()
    {
        Assert.AreSequenceEqual([2L, 6L, 10L, 14L, 18L], StartMeasures(Propose(Chart(DrumsVariant.FiveLane, crashPad: Green), DrumsVariant.FiveLane)));
        Assert.AreEqual(NoFillsReason.NoSpotFits, Propose(Chart(DrumsVariant.FiveLane, crashPad: FifthPad), DrumsVariant.FiveLane).Reason);
    }

    [TestMethod]
    public void InAnyNoteModeAFillEndsOnAnyNoteButA2xKick()
    {
        var reds = Chart(DrumsVariant.Pro, crashPad: Red);
        Assert.AreEqual(NoFillsReason.NoSpotFits, Propose(reds, DrumsVariant.Pro).Reason);
        Assert.AreSequenceEqual([2L, 6L, 10L, 14L, 18L], StartMeasures(Propose(reds, DrumsVariant.Pro, new FillOptions(EndOn: FillEnd.AnyNote))));

        var doubleKicks = Chart(DrumsVariant.Pro, crashPad: null);
        for (long m = 2; m <= 20; m++)
            doubleKicks.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.Add(new EditableNote(m * Measure, 1, Kick, DoubleKick: true));
        Assert.AreEqual(NoFillsReason.NoSpotFits, Propose(doubleKicks, DrumsVariant.Pro, new FillOptions(EndOn: FillEnd.AnyNote)).Reason);
    }

    [TestMethod]
    public void ASpotIsLeftOutWhenTheMeasuresFirstBeatHasNoCrash()
    {
        var chart = Chart(DrumsVariant.Pro);
        var expert = chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        expert.Notes.Remove(new EditableNote(3 * Measure, 1, Green));
        expert.Notes.Add(new EditableNote((3 * Measure) + 240, 1, Green));

        Assert.AreSequenceEqual([3L, 7L, 11L, 15L, 19L], StartMeasures(Propose(chart, DrumsVariant.Pro)));
    }

    [TestMethod]
    public void ASpotIsLeftOutWhenALowerDifficultyWithNotesHasNoneThere()
    {
        var chart = Chart(DrumsVariant.Pro);
        var hard = chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Hard];
        for (long m = 0; m <= 20; m++)
        {
            if (m != 3)
                hard.Notes.Add(new EditableNote(m * Measure, 1, m % 2 == 0 ? Kick : Red));
        }

        // Hard needn't have the crash, only a note.
        Assert.AreSequenceEqual([3L, 7L, 11L, 15L, 19L], StartMeasures(Propose(chart, DrumsVariant.Pro)));
    }

    [TestMethod]
    public void FillsAreSpacedFromEachOtherAndNeverOverlap()
    {
        var chart = Chart(DrumsVariant.Pro);

        Assert.AreSequenceEqual([2L, 4L, 6L, 8L, 10L, 12L, 14L, 16L, 18L], StartMeasures(Propose(chart, DrumsVariant.Pro, new FillOptions(SpacingMeasures: 2))));

        // Two measures long, a measure apart: the next can't start on the last one's activation note.
        var generated = Propose(chart, DrumsVariant.Pro, new FillOptions(SpacingMeasures: 1, LengthMeasures: 2));
        Assert.AreSequenceEqual([2L, 5L, 8L, 11L, 14L, 17L], StartMeasures(generated));
        Assert.IsTrue(generated.Proposals.Zip(generated.Proposals.Skip(1)).All(p => !p.First.Fill.Overlaps(p.Second.Fill)));
    }

    [TestMethod]
    public void FillsAreSpacedFromTheFillsAlreadyThere()
    {
        var chart = Chart(DrumsVariant.Pro);
        var existing = new Phrase(PhraseKind.DrumFill, (5 * Measure) + 480, 960);
        chart.PartOf(ChartInstrument.Drums)!.Phrases.Add(existing);

        var generated = Propose(chart, DrumsVariant.Pro);

        // It starts a quarter into measure 5, so measure 10 is the first line four measures past it.
        Assert.AreSequenceEqual([10L, 14L, 18L], StartMeasures(generated));
    }

    [TestMethod]
    public void AFillsLengthIsInMeasuresOfTheTimeSignatureInForce()
    {
        // 4/4 to measure 4, then 3/4: measure lines at 7680, 9120, 10560 and on, 1,440 apart.
        var chart = Chart(DrumsVariant.Pro, crashPad: null);
        new AddTimeSignature(new TimeSignatureMarker(4 * Measure, 3, 4)).Apply(chart);
        var expert = chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        foreach (var line in chart.TempoMap.ToTempoMap().BeatLines(0, 40_000).Where(b => b.IsMeasure))
            expert.Notes.Add(new EditableNote(line.Tick, 1, Green));

        var generated = Propose(chart, DrumsVariant.Pro, new FillOptions(SpacingMeasures: 3, LengthMeasures: 2));

        Assert.AreEqual(new Phrase(PhraseKind.DrumFill, 2 * Measure, (4 * Measure) + 1 - (2 * Measure)), generated.Proposals[0].Fill);
        Assert.AreEqual(new Phrase(PhraseKind.DrumFill, 9120, 12000 + 1 - 9120), generated.Proposals[1].Fill);
    }

    [TestMethod]
    public void ASpanLimitsTheFillsToIt()
    {
        var chart = Chart(DrumsVariant.Pro);

        var generated = Propose(chart, DrumsVariant.Pro, span: ((9 * Measure) + 1, 17 * Measure));

        Assert.AreSequenceEqual([10L, 14L], StartMeasures(generated));
        Assert.IsTrue(generated.Proposals.All(p => p.Fill.Tick > 9 * Measure && p.ActivationTick <= 17 * Measure));
    }

    [TestMethod]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.FiveLane)]
    public void GeneratedFillsGiveNoChr042OrChr043(DrumsVariant variant)
    {
        // A busy chart on every difficulty, with star power here and there and the lower difficulties thinner.
        var random = new Random(6);
        var chart = Chart(variant, crashPad: null, starPower: [FirstStarPower, SecondStarPower, new Phrase(PhraseKind.StarPower, 9 * Measure, 2400), new Phrase(PhraseKind.StarPower, 21 * Measure, 960)]);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        foreach (var track in drums.Tracks)
        {
            int every = 4 - (int)track.Difficulty;
            for (long beat = 0; beat < 30 * 4; beat++)
            {
                if (beat % every == 0)
                    track.Notes.Add(new EditableNote(beat * 480, 1, beat % 4 == 0 && random.Next(3) > 0 ? Green : random.Next(0, 4)));
            }
        }

        var generated = Propose(chart, variant, new FillOptions(SpacingMeasures: 2, EndOn: FillEnd.Crash));
        Assert.IsNotEmpty(generated.Proposals);
        foreach (var proposal in generated.Proposals)
            new AddPhrase(ChartInstrument.Drums, proposal.Fill).Apply(chart);

        var songIni = new[] { KeyValuePair.Create("pro_drums", variant == DrumsVariant.Pro ? "True" : "False"), KeyValuePair.Create("five_lane_drums", variant == DrumsVariant.FiveLane ? "True" : "False") };
        var result = new ChartLinter().LintAll(new ChartLintInput(chart.Clone(), songIni));
        Assert.IsEmpty(result.FindingsOf(ChrRules.Chr042));
        Assert.IsEmpty(result.FindingsOf(ChrRules.Chr043));
    }

    [TestMethod]
    public void AddKeepsClearOfAFillAlreadyThereWhileReplaceIgnoresIt()
    {
        var chart = Chart(DrumsVariant.Pro);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var existing = new Phrase(PhraseKind.DrumFill, 4 * Measure, Measure);
        drums.Phrases.Add(existing);

        var added = Propose(chart, DrumsVariant.Pro, mode: GenerationMode.Add);
        Assert.IsFalse(added.Proposals.Any(p => p.Fill.Overlaps(existing)));
        Assert.AreSequenceEqual([8L, 12L, 16L], StartMeasures(added));

        var replaced = Propose(chart, DrumsVariant.Pro, mode: GenerationMode.Replace);
        Assert.AreSequenceEqual([2L, 6L, 10L, 14L, 18L], StartMeasures(replaced));
    }

    /// <summary>
    /// Drums of <paramref name="variant"/> with star power over measures 0 and 1 (or <paramref name="starPower"/>), and on
    /// Expert a note on <paramref name="crashPad"/> on the first beat of measures 0 to 20, with a kick and a red between;
    /// no notes when it's null.
    /// </summary>
    private static EditableChart Chart(DrumsVariant variant, int? crashPad = Green, Phrase[]? starPower = null)
    {
        var chart = Drums(variant);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var expert = drums[ChartDifficulty.Expert];
        for (long m = 0; m <= 20 && crashPad is { } pad; m++)
        {
            expert.Notes.Add(new EditableNote(m * Measure, 1, pad));
            expert.Notes.Add(new EditableNote((m * Measure) + 480, 1, Kick));
            expert.Notes.Add(new EditableNote((m * Measure) + 960, 1, Red));
        }

        foreach (var phrase in starPower ?? [FirstStarPower, SecondStarPower])
            drums.Phrases.Add(phrase);
        return chart;
    }

    private static FillGeneration Propose(EditableChart chart, DrumsVariant variant, FillOptions? options = null, (long From, long To)? span = null, GenerationMode mode = GenerationMode.Add) =>
        FillGenerator.Propose(chart, variant, options ?? FillOptions.Default, span, mode);

    private static List<long> StartMeasures(FillGeneration generated) => [.. generated.Proposals.Select(p => p.Fill.Tick / Measure)];
}
