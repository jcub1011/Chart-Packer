using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;
using static ChartBuilder.Tests.Charting.TrackCommandTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>Each rule of Reduce (charter-spec C9 item 2), on small charts.</summary>
[TestClass]
public sealed class ReducerTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Medium = ChartDifficulty.Medium;
    private const ChartDifficulty Easy = ChartDifficulty.Easy;
    private const int G = 0, R = 1, Y = 2, B = 3, O = 4;

    private static readonly FretReductionRules NoThinning = FretReductionRules.Default(ChartInstrumentKind.FiveFret, Hard) with { ThinningDivision = 0, MaxChordSize = 5 };

    [TestMethod]
    [DataRow(0L, 0)]
    [DataRow(1920L, 0)]
    [DataRow(480L, 1)]
    [DataRow(240L, 2)]
    [DataRow(120L, 3)]
    [DataRow(160L, 3)]
    [DataRow(60L, 4)]
    [DataRow(7L, Reducer.OffGrid)]
    public void APositionsStrengthFollowsTheMeasureTheBeatAndItsHalvings(long tick, int strength)
    {
        var map = NewChart(Guitar).TempoMap.ToTempoMap();

        Assert.AreEqual(strength, Reducer.BeatStrength(tick, map));
    }

    [TestMethod]
    public void ThinningKeepsDownbeatsOverTheNotesBetween()
    {
        var map = NewChart(Guitar).TempoMap.ToTempoMap();

        // Sixteenths from just before a beat: the beats win, though the sixteenth before each comes first.
        var kept = Reducer.Thin([360, 480, 600, 720, 840, 960, 1080], 480, map);

        Assert.AreSequenceEqual([480L, 960L], kept.ToList());
    }

    [TestMethod]
    public void ThinningLeavesNoTwoTicksCloserThanTheGrid()
    {
        var map = NewChart(Guitar).TempoMap.ToTempoMap();
        var random = new Random(7);
        long[] ticks = [.. Enumerable.Range(0, 400).Select(_ => (long)random.Next(0, 20_000))];

        var kept = Reducer.Thin(ticks, 240, map);

        for (int i = 1; i < kept.Count; i++)
            Assert.IsGreaterThanOrEqualTo(240L, kept[i] - kept[i - 1]);
    }

    [TestMethod]
    public void AChordKeepsItsOutermostFretsAndMovesInsideTheRange()
    {
        var chart = Frets(Chord(0, G, O), Chord(480, B, O), Chord(960, G, Y, O));
        var medium = Rules(Medium) with { ThinningDivision = 0 };
        var easy = Rules(Easy) with { ThinningDivision = 0 };

        var toMedium = Reducer.Reduce(chart, Guitar, Expert, Medium, medium).Notes;
        var toEasy = Reducer.Reduce(chart, Guitar, Expert, Easy, easy).Notes;

        Assert.AreSequenceEqual([G, B], LanesAt(toMedium, 0));
        Assert.AreSequenceEqual([Y, B], LanesAt(toMedium, 480));
        Assert.AreSequenceEqual([G, B], LanesAt(toMedium, 960));
        Assert.AreSequenceEqual([G], LanesAt(toEasy, 0));
        Assert.AreSequenceEqual([Y], LanesAt(toEasy, 480));
    }

    [TestMethod]
    public void ASingleNotePastTheRangeMovesDownToItsTop()
    {
        var chart = Frets(Chord(0, O));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([Y], LanesAt(notes, 0));
    }

    [TestMethod]
    public void AnOpenNoteStaysOpen()
    {
        var chart = Frets(Chord(0, ChartNote.OpenLane));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([ChartNote.OpenLane], LanesAt(notes, 0));
    }

    [TestMethod]
    public void OnGhlAWhiteAndABlackOnOneColumnCountOnce()
    {
        // W1 with B1 (a barre) and W3: two columns.
        var chart = NewChart(ChartInstrument.GhlGuitar);
        Add(chart, ChartInstrument.GhlGuitar, Expert, Note(0, 0), Note(0, 3), Note(0, 2));
        var rules = FretReductionRules.Default(ChartInstrumentKind.SixFret, Medium) with { ThinningDivision = 0 };

        var notes = Reducer.Reduce(chart, ChartInstrument.GhlGuitar, Expert, Medium, rules).Notes;
        var easy = Reducer.Reduce(chart, ChartInstrument.GhlGuitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.SixFret, Easy) with { ThinningDivision = 0 }).Notes;

        Assert.AreSequenceEqual([0, 2, 3], LanesAt(notes, 0));
        Assert.AreSequenceEqual([0, 3], LanesAt(easy, 0));
    }

    [TestMethod]
    public void ForcingAndTapsAreRemovedBelowHardButAForcedStrumStays()
    {
        var chart = Frets(Chord(0, G), Chord(480, R), Chord(960, Y));
        Mark(chart, Guitar, Expert, 0, ChordFlags.ForceHopo);
        Mark(chart, Guitar, Expert, 480, ChordFlags.Tap);
        Mark(chart, Guitar, Expert, 960, ChordFlags.ForceStrum);

        var hard = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard)).Flags;
        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium)).Flags;

        Assert.HasCount(3, hard);
        Assert.AreSequenceEqual([new FlaggedChord(960, ChordFlags.ForceStrum)], medium.ToList());
    }

    [TestMethod]
    public void ShortSustainsBecomePlainNotesOnEasy()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 240, G), new EditableNote(960, 960, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([EditableNote.PlainLength, 960L], notes.Select(n => n.Length).ToList());
    }

    [TestMethod]
    public void ASustainPressedIntoTheNextNoteOnItsLaneEndsThere()
    {
        // On Easy blue and orange both move down to yellow, so the blue sustain runs into the note after it.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 960, B), new EditableNote(480, 1, O));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { ThinningDivision = 0 }).Notes;

        Assert.AreEqual(480, notes.First(n => n.Tick == 0).Length);
    }

    [TestMethod]
    public void DrumsKeepRedThenCymbalsThenToms()
    {
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(0, 0), Note(0, 1), Note(0, 3), Note(0, 4), Note(480, 2), Note(480, 3), Note(480, 4));
        new SetToms([(3, 0L), (2, 480L), (3, 480L)], tom: true, Expert).Apply(chart);
        var easy = DrumReductionRules.Default(Easy) with { ThinningDivision = 0, KickDivision = 0 };
        var medium = DrumReductionRules.Default(Medium) with { ThinningDivision = 0, KickDivision = 0 };

        var toEasy = Reducer.Reduce(chart, Expert, Easy, easy, DrumsVariant.Pro).Notes;
        var toMedium = Reducer.Reduce(chart, Expert, Medium, medium, DrumsVariant.Pro).Notes;

        Assert.AreSequenceEqual([0, 1], LanesAt(toEasy, 0));
        Assert.AreSequenceEqual([4], LanesAt(toEasy, 480));
        Assert.AreSequenceEqual([0, 1, 4], LanesAt(toMedium, 0));
        Assert.AreSequenceEqual([2, 4], LanesAt(toMedium, 480));
    }

    [TestMethod]
    public void TheKickIsThinnedOnItsOwnGrid()
    {
        var chart = DrumsChart(DrumsVariant.FourLane);
        Add(chart, Drums, Expert, [.. Enumerable.Range(0, 8).Select(i => Note(i * 240L, 0)), .. Enumerable.Range(0, 4).Select(i => Note(i * 480L, 1))]);
        var rules = DrumReductionRules.Default(Medium) with { ThinningDivision = 16 };

        var notes = Reducer.Reduce(chart, Expert, Medium, rules, DrumsVariant.FourLane).Notes;

        Assert.AreSequenceEqual([0L, 240L, 480L, 720L, 960L, 1200L, 1440L, 1680L], notes.Where(n => n.Lane == 0).Select(n => n.Tick).ToList());

        var easy = Reducer.Reduce(chart, Expert, Easy, DrumReductionRules.Default(Easy) with { ThinningDivision = 8 }, DrumsVariant.FourLane).Notes;
        Assert.AreSequenceEqual([0L, 480L, 960L, 1440L], easy.Where(n => n.Lane == 0).Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void TheTwoTimesKickIsLeftOutAndDiscoFlipIsCarried()
    {
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(0, 0), new EditableNote(0, 1, 0, DoubleKick: true), Note(480, 1));
        new AddDiscoFlip(Expert, 0, 960).Apply(chart);

        var result = Reducer.Reduce(chart, Expert, Hard, DrumReductionRules.Default(Hard), DrumsVariant.Pro);

        Assert.DoesNotContain(n => n.DoubleKick, result.Notes);
        Assert.AreSequenceEqual(chart.PartOf(Drums)![Expert].Mixes.ToList(), [.. result.Mixes]);
        Assert.AreEqual(1, result.Removed);
    }

    [TestMethod]
    public void InsideADiscoFlipTheRedThePlayerSeesIsKept()
    {
        // Stored yellow is played as red inside the flip; stored red as a yellow cymbal.
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(0, 1), Note(0, 2));
        new AddDiscoFlip(Expert, 0, 480).Apply(chart);

        var notes = Reducer.Reduce(chart, Expert, Easy, DrumReductionRules.Default(Easy) with { ThinningDivision = 0 }, DrumsVariant.Pro).Notes;

        Assert.AreSequenceEqual([2], LanesAt(notes, 0));
    }

    [TestMethod]
    public void ReducingToTheSameOrAHigherDifficultyIsRefused()
    {
        var chart = Frets(Chord(0, G));

        Assert.Throws<ArgumentException>(() => Reducer.Reduce(chart, Guitar, Medium, Hard, Rules(Hard)));
    }

    [TestMethod]
    public void NormalizedKeepsEveryValueToWhatItCanBe()
    {
        var bad = new ReductionDefaults
        {
            FiveFretEasy = new FretReductionRules { MaxChordSize = 9, HighestLane = -2, ThinningDivision = 15 },
            SixFretHard = new FretReductionRules { MaxChordSize = 5, HighestLane = 4 },
            DrumsMedium = new DrumReductionRules { PadsPerHit = 0, ThinningDivision = -1, KickDivision = 9, KeepDoubleKick = true },
            FiveFretHard = null!,
        };

        var fixedUp = bad.Normalized();

        Assert.AreEqual(5, fixedUp.FiveFretEasy.MaxChordSize);
        Assert.AreEqual(0, fixedUp.FiveFretEasy.HighestLane);
        Assert.AreEqual(16, fixedUp.FiveFretEasy.ThinningDivision);
        Assert.AreEqual(3, fixedUp.SixFretHard.MaxChordSize);
        Assert.AreEqual(2, fixedUp.SixFretHard.HighestLane);
        Assert.AreEqual(1, fixedUp.DrumsMedium.PadsPerHit);
        Assert.AreEqual(0, fixedUp.DrumsMedium.ThinningDivision);
        Assert.AreEqual(8, fixedUp.DrumsMedium.KickDivision);
        Assert.IsFalse(fixedUp.DrumsMedium.KeepDoubleKick);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretHard, fixedUp.FiveFretHard);
    }

    [TestMethod]
    public void TheShippedDefaultsAreTheSpecsTable()
    {
        var shipped = ReductionDefaults.Shipped;

        Assert.AreSequenceEqual([2, 2, 1], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.MaxChordSize).ToList());
        Assert.AreSequenceEqual([4, 3, 2], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.HighestLane).ToList());
        Assert.AreSequenceEqual([2, 2, 1], new[] { shipped.SixFretHard, shipped.SixFretMedium, shipped.SixFretEasy }.Select(r => r.HighestLane).ToList());
        Assert.AreSequenceEqual([16, 8, 4], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.ThinningDivision).ToList());
        Assert.AreSequenceEqual([true, false, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepForcedHopoAndTap).ToList());
        Assert.AreSequenceEqual([true, true, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepShortSustains).ToList());
        Assert.AreSequenceEqual([2, 2, 1], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.PadsPerHit).ToList());
        Assert.AreSequenceEqual([0, 8, 4], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.KickDivision).ToList());
    }

    private static FretReductionRules Rules(ChartDifficulty target) => FretReductionRules.Default(ChartInstrumentKind.FiveFret, target);

    private static (long Tick, int[] Lanes) Chord(long tick, params int[] lanes) => (tick, lanes);

    private static EditableChart Frets(params (long Tick, int[] Lanes)[] chords)
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, [.. chords.SelectMany(c => c.Lanes.Select(l => Note(c.Tick, l)))]);
        return chart;
    }

    private static EditableChart DrumsChart(DrumsVariant variant) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Drums }, variant));

    private static List<int> LanesAt(IReadOnlyList<EditableNote> notes, long tick) => [.. notes.Where(n => n.Tick == tick).Select(n => n.Lane).Order()];
}
