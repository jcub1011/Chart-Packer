using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
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
        // With green alone allowed, the green sustain runs into the red after it, pressed down to green.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 960, G), new EditableNote(480, 1, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Free(Easy) with { HighestLane = 0 }).Notes;

        Assert.AreEqual(480, notes.First(n => n.Tick == 0).Length);
    }

    [TestMethod]
    public void ThinningKeepsTheMinimumGapInMsAtFastTempos()
    {
        // At 240 BPM a beat is 250 ms, so Easy's 1/4 grid alone keeps every beat; 400 ms keeps no two closer than 768 ticks.
        var chart = Frets(240, [.. Enumerable.Range(0, 8).Select(i => Chord(i * 480L, G))]);

        var byGrid = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 0 }).Notes;
        var byTime = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 400 }).Notes;

        Assert.HasCount(8, byGrid);
        Assert.IsLessThan(8, byTime.Count);
        AssertGapsAtLeast(768, byTime);
    }

    [TestMethod]
    public void TheLargerOfTheGridAndTheGapInMsHolds()
    {
        // At 120 BPM a beat is 500 ms: past 400 ms, so the grid holds; 600 ms is past the beat, so it holds.
        var chart = Frets(120, [.. Enumerable.Range(0, 8).Select(i => Chord(i * 480L, G))]);

        var byGrid = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 400 }).Notes;
        var byTime = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 600 }).Notes;

        Assert.HasCount(8, byGrid);
        AssertGapsAtLeast(576, byTime);
    }

    [TestMethod]
    public void AGapInMsFollowsATempoChange()
    {
        // 120 BPM for a measure, then 240: each beat is kept before the change, and no two closer than 400 ms after it.
        var chart = Frets(120, [.. Enumerable.Range(0, 16).Select(i => Chord(i * 480L, G))]);
        new AddTempo(new TempoChange(1920, 250_000)).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 400 }).Notes;

        Assert.AreSequenceEqual([0L, 480L, 960L, 1440L, 1920L], notes.Select(n => n.Tick).Where(t => t <= 1920).ToList());
        AssertGapsAtLeast(768, [.. notes.Where(n => n.Tick >= 1920)]);
    }

    [TestMethod]
    public void ATrillPastTheRangeMovesDownAsAWholeAndStaysATrill()
    {
        // On Medium an orange–blue trill was blue–blue–blue–blue; moved down as a phrase, it's blue–yellow.
        var chart = Frets(Chord(0, O), Chord(240, B), Chord(480, O), Chord(720, B));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium)).Notes;

        Assert.AreSequenceEqual([B, Y, B, Y], Lanes(notes));
    }

    [TestMethod]
    public void ARunWiderThanTheRangeKeepsRising()
    {
        // On Easy green to orange was green–red–yellow–yellow–yellow; fitted to the range, it rises over all three frets.
        var chart = Frets(Chord(0, G), Chord(240, R), Chord(480, Y), Chord(720, B), Chord(960, O));

        var lanes = Lanes(Reducer.Reduce(chart, Guitar, Expert, Easy, Free(Easy)).Notes);

        Assert.HasCount(5, lanes);
        for (int i = 1; i < lanes.Count; i++)
            Assert.IsGreaterThanOrEqualTo(lanes[i - 1], lanes[i], $"Note {i} goes down.");
        Assert.AreSequenceEqual([G, R, Y], lanes.Distinct().ToList());
        Assert.AreNotEqual("0,1,2,2,2", string.Join(",", lanes));
    }

    [TestMethod]
    public void APhraseInsideTheRangeDoesNotMove()
    {
        var chart = Frets(Chord(0, G), Chord(240, R), Chord(480, Y), Chord(720, R), Chord(960, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium)).Notes;

        Assert.AreSequenceEqual([G, R, Y, R, G], Lanes(notes));
    }

    [TestMethod]
    public void ARestSplitsPhrases()
    {
        // A trill past blue, a rest of a measure, then a green–red trill: the second phrase isn't moved with the first.
        var chart = Frets(Chord(0, O), Chord(240, B), Chord(480, O), Chord(720, B), Chord(1920, G), Chord(2160, R), Chord(2400, G), Chord(2640, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium)).Notes;

        Assert.AreSequenceEqual([B, Y, B, Y, G, R, G, R], Lanes(notes));
    }

    [TestMethod]
    public void ASectionSplitsPhrases()
    {
        // As one phrase, green to orange is wider than Medium's range and every note would move; a section between them
        // leaves the green–red trill where it is.
        var chart = Frets(Chord(0, O), Chord(240, B), Chord(480, O), Chord(720, B), Chord(960, G), Chord(1200, R), Chord(1440, G), Chord(1680, R));
        new AddTextEvent(null, TextEvent.Of(960, "[section Verse]")).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium)).Notes;

        Assert.AreSequenceEqual([B, Y, B, Y, G, R, G, R], Lanes(notes));
    }

    [TestMethod]
    public void ChordsKeepTheirShapeInAPhraseMovedDown()
    {
        // Yellow–orange then red–blue: past blue on Medium, both move down one, still a fret apart.
        var chart = Frets(Chord(0, Y, O), Chord(240, R, B));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium)).Notes;

        Assert.AreSequenceEqual([R, B], LanesAt(notes, 0));
        Assert.AreSequenceEqual([G, Y], LanesAt(notes, 240));
    }

    [TestMethod]
    public void OnGhlAPhraseMovedDownKeepsItsColours()
    {
        // W2 then B3 (lane 5): past column 2 on Easy, both move down a column, a white and a black still.
        var chart = NewChart(ChartInstrument.GhlGuitar);
        Add(chart, ChartInstrument.GhlGuitar, Expert, Note(0, 1), Note(240, 5));
        var rules = FretReductionRules.Default(ChartInstrumentKind.SixFret, Easy) with { ThinningDivision = 0, MinGapMs = 0 };

        var notes = Reducer.Reduce(chart, ChartInstrument.GhlGuitar, Expert, Easy, rules).Notes;

        Assert.AreSequenceEqual([0, 4], Lanes(notes));
    }

    [TestMethod]
    public void ALongSustainOnTheAndBeatsAShortNoteOnTheBeat()
    {
        // Ranked by position alone, the plain green on beat 2 took the place of the red sustain an eighth after it.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, G), Note(480, G), new EditableNote(720, 960, R), Note(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 0 }).Notes;

        Assert.AreSequenceEqual([0L, 720L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void AChordBeatsASingleNoteOnAsStrongABeat()
    {
        // Sixteenths a quarter's grid apart: the chord is kept over the green before it.
        var chart = Frets(Chord(600, G), Chord(1080, G), Chord(1320, G, Y), Chord(1800, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 0 }).Notes;

        Assert.AreSequenceEqual([600L, 1320L, 1800L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void TheFirstNoteAfterARestIsKept()
    {
        // A rest after the first green, then red on beat 4 and yellow on the next measure: red is kept, as it starts a phrase.
        var chart = Frets(Chord(0, G), Chord(1440, R), Chord(1920, Y), Chord(2880, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { ThinningDivision = 2, MinGapMs = 0 }).Notes;

        Assert.AreSequenceEqual([0L, 1440L, 2880L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void WhereTheMelodyTurnsIsKept()
    {
        // Sixteenths green, red, yellow, red: yellow, where it turns, is kept over the red before it.
        var chart = Frets(Chord(600, G), Chord(1080, R), Chord(1320, Y), Chord(1800, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard) with { ThinningDivision = 4, MinGapMs = 0 }).Notes;

        Assert.AreSequenceEqual([600L, 1320L, 1800L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void APhrasesLastNoteIsKept()
    {
        // Sixteenths a quarter's grid apart before a rest: the second, which ends the phrase, is kept.
        var chart = Frets(Chord(600, G), Chord(1080, R), Chord(1320, Y), Chord(3000, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { MinGapMs = 0 }).Notes;

        Assert.AreSequenceEqual([600L, 1320L, 3000L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void EveryStarPowerPhraseAndSoloKeepsANote()
    {
        // Each phrase covers a sixteenth only, which thinning to quarters drops beside the beats either side of it (AC C9.3).
        var chart = Frets(Chord(0, G), Chord(120, R), Chord(480, G), Chord(960, G), Chord(1440, G), Chord(1560, Y), Chord(1920, G));
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 100, 40)).Apply(chart);
        new AddPhrase(Guitar, new Phrase(PhraseKind.Solo, 1500, 100)).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.Contains(n => n.Tick == 120, notes);
        Assert.Contains(n => n.Tick == 1560, notes);
        AssertGapsAtLeast(480, notes);
    }

    [TestMethod]
    public void PhrasesCloserThanTheGridStillLeaveNoTwoChordsCloser()
    {
        var chart = Frets(Chord(0, G), Chord(120, R), Chord(480, Y));
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 0, 60)).Apply(chart);
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 100, 60)).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        AssertGapsAtLeast(480, notes);
    }

    [TestMethod]
    public void EveryStarPowerPhraseKeepsAHitOnDrums()
    {
        var chart = DrumsChart(DrumsVariant.FourLane);
        Add(chart, Drums, Expert, Note(0, 0), Note(0, 1), Note(120, 2), Note(480, 1), Note(960, 1));
        new AddPhrase(Drums, new Phrase(PhraseKind.StarPower, 100, 40)).Apply(chart);

        var notes = Reducer.Reduce(chart, Expert, Easy, DrumReductionRules.Default(Easy), DrumsVariant.FourLane).Notes;

        Assert.Contains(n => n.Tick == 120, notes);
        AssertGapsAtLeast(480, notes);
    }

    [TestMethod]
    public void ARangeLeavesTheTargetOutsideItAlone()
    {
        var chart = Frets([.. Enumerable.Range(0, 17).Select(i => Chord(i * 240L, i % 5))]);
        Add(chart, Guitar, Easy, Note(0, Y), Note(3840, R));

        var result = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy), (960, 2880));

        Assert.AreSequenceEqual([new EditableNote(0, 1, Y), new EditableNote(3840, 1, R)], result.Notes.Where(n => n.Tick is < 960 or > 2880).ToList());
        Assert.IsNotEmpty(result.Notes.Where(n => n.Tick is >= 960 and <= 2880));
        Assert.AreEqual(Expert9To24().Length - result.Notes.Count(n => n.Tick is >= 960 and <= 2880), result.Removed);

        static long[] Expert9To24() => [.. Enumerable.Range(4, 9).Select(i => i * 240L)];
    }

    [TestMethod]
    public void ARangeKeepsTheGapAgainstTheTargetEitherSide()
    {
        var chart = Frets([.. Enumerable.Range(0, 17).Select(i => Chord(i * 240L, G))]);
        Add(chart, Guitar, Easy, Note(900, G), Note(3000, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy), (960, 2880)).Notes;

        AssertGapsAtLeast(480, notes);
    }

    [TestMethod]
    public void ATargetsSustainRunningIntoTheRangeEndsAtItsFirstNoteOnTheLane()
    {
        var chart = Frets(Chord(960, G), Chord(1440, R));
        Add(chart, Guitar, Easy, new EditableNote(0, 1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy), (960, 2000)).Notes;

        Assert.AreEqual(960, notes.First(n => n.Tick == 0).Length);
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
            FiveFretEasy = new FretReductionRules { MaxChordSize = 9, HighestLane = -2, ThinningDivision = 15, MinGapMs = 5000 },
            FiveFretMedium = new FretReductionRules { MinGapMs = null },
            SixFretHard = new FretReductionRules { MaxChordSize = 5, HighestLane = 4 },
            DrumsMedium = new DrumReductionRules { PadsPerHit = 0, ThinningDivision = -1, KickDivision = 9, KeepDoubleKick = true },
            FiveFretHard = null!,
        };

        var fixedUp = bad.Normalized();

        Assert.AreEqual(5, fixedUp.FiveFretEasy.MaxChordSize);
        Assert.AreEqual(0, fixedUp.FiveFretEasy.HighestLane);
        Assert.AreEqual(16, fixedUp.FiveFretEasy.ThinningDivision);
        Assert.AreEqual(ReductionDefaults.MaxGapMs, fixedUp.FiveFretEasy.MinGapMs);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretMedium.MinGapMs, fixedUp.FiveFretMedium.MinGapMs);
        Assert.AreEqual(ReductionDefaults.Shipped.DrumsMedium.MinGapMs, fixedUp.DrumsMedium.MinGapMs);
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
        Assert.AreSequenceEqual([100, 200, 400], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.MinGapMs ?? 0).ToList());
        Assert.AreSequenceEqual([100, 200, 400], new[] { shipped.SixFretHard, shipped.SixFretMedium, shipped.SixFretEasy }.Select(r => r.MinGapMs ?? 0).ToList());
        Assert.AreSequenceEqual([100, 200, 400], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.MinGapMs ?? 0).ToList());
        Assert.AreSequenceEqual([true, false, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepForcedHopoAndTap).ToList());
        Assert.AreSequenceEqual([true, true, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepShortSustains).ToList());
        Assert.AreSequenceEqual([2, 2, 1], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.PadsPerHit).ToList());
        Assert.AreSequenceEqual([0, 8, 4], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.KickDivision).ToList());
    }

    private static FretReductionRules Rules(ChartDifficulty target) => FretReductionRules.Default(ChartInstrumentKind.FiveFret, target);

    // The rules for target with no thinning, by grid or by time.
    private static FretReductionRules Free(ChartDifficulty target) => Rules(target) with { ThinningDivision = 0, MinGapMs = 0 };

    private static (long Tick, int[] Lanes) Chord(long tick, params int[] lanes) => (tick, lanes);

    private static EditableChart Frets(params (long Tick, int[] Lanes)[] chords) => Frets(120, chords);

    private static EditableChart Frets(int bpm, params (long Tick, int[] Lanes)[] chords)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, bpm, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.FourLane));
        Add(chart, Guitar, Expert, [.. chords.SelectMany(c => c.Lanes.Select(l => Note(c.Tick, l)))]);
        return chart;
    }

    // Each note's lane, in tick order.
    private static List<int> Lanes(IReadOnlyList<EditableNote> notes) => [.. notes.OrderBy(n => n.Tick).ThenBy(n => n.Lane).Select(n => n.Lane)];

    private static void AssertGapsAtLeast(long ticks, IReadOnlyList<EditableNote> notes)
    {
        var chords = notes.Select(n => n.Tick).Distinct().Order().ToList();
        for (int i = 1; i < chords.Count; i++)
            Assert.IsGreaterThanOrEqualTo(ticks, chords[i] - chords[i - 1], $"Chords at {chords[i - 1]} and {chords[i]}.");
    }

    private static EditableChart DrumsChart(DrumsVariant variant) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Drums }, variant));

    private static List<int> LanesAt(IReadOnlyList<EditableNote> notes, long tick) => [.. notes.Where(n => n.Tick == tick).Select(n => n.Lane).Order()];
}
