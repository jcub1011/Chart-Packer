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
    private const int G = 0, R = 1, Y = 2, B = 3, O = 4, Open = ChartNote.OpenLane;

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
    public void OpensBetweenPressedNotesAreGreenWithTheFretsOneUp()
    {
        // Eighths going between a fret and an open, as in Bloom's bars 20 and 21: on Hard each open is green and each fret
        // one up, so going between them is still a move, as the song's own Hard has it.
        var notes = Reducer.Reduce(OpensBetweenFrets(), Guitar, Expert, Hard, Free(Hard)).Notes;

        Assert.AreSequenceEqual([Y, G, Y, G, R, G, R, G, Y, G, Y, G, B, G, B, G], Lanes(notes));
    }

    [TestMethod]
    public void APhraseWhoseOpensAreThinnedOutKeepsItsFretsOneUp()
    {
        // Thinned to the beats, no open is left, but the phrase had them: its frets are one up, as on Hard.
        var notes = Reducer.Reduce(OpensBetweenFrets(), Guitar, Expert, Medium, Rules(Medium) with { ThinningDivision = 4 }).Notes;

        Assert.AreSequenceEqual([Y, Y, R, R, Y, Y, B, B], Lanes(notes));
    }

    [TestMethod]
    public void APhraseOfOpensAloneKeepsThem()
    {
        // Opens, a rest, then frets: two phrases, neither with both, so nothing is fretted or moved.
        var chart = Frets(Chord(0, Open), Chord(240, Open), Chord(480, Open), Chord(1920, R), Chord(2160, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Hard, Free(Hard)).Notes;

        Assert.AreSequenceEqual([Open, Open, Open, R, G], Lanes(notes));
    }

    [TestMethod]
    public void WithTheRuleOffOpensBetweenPressedNotesAreKept()
    {
        var notes = Reducer.Reduce(OpensBetweenFrets(), Guitar, Expert, Hard, Free(Hard) with { KeepOpenNotes = true }).Notes;

        Assert.AreSequenceEqual([R, Open, R, Open, G, Open, G, Open, R, Open, R, Open, Y, Open, Y, Open], Lanes(notes));
    }

    [TestMethod]
    public void AnOpenSustainFrettedIsAGreenSustain()
    {
        var chart = Frets(Chord(960, R));
        Add(chart, Guitar, Expert, new EditableNote(0, 900, Open));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Hard, Free(Hard)).Notes;

        Assert.AreEqual(new EditableNote(0, 900, G), notes.Single(n => n.Tick == 0));
        Assert.AreEqual(Y, notes.Single(n => n.Tick == 960).Lane);
    }

    [TestMethod]
    public void OnGhlAnOpenBetweenPressedNotesIsTheFirstWhite()
    {
        // W1, open, W2, open, B1: the opens are W1, and each fret a column up, a black staying black.
        var chart = NewChart(ChartInstrument.GhlGuitar);
        Add(chart, ChartInstrument.GhlGuitar, Expert, Note(0, 0), Note(240, Open), Note(480, 1), Note(720, Open), Note(960, 3));
        var rules = FretReductionRules.Default(ChartInstrumentKind.SixFret, Hard) with { ThinningDivision = 0 };

        var notes = Reducer.Reduce(chart, ChartInstrument.GhlGuitar, Expert, Hard, rules).Notes;

        Assert.AreSequenceEqual([1, 0, 2, 0, 4], Lanes(notes));
    }

    [TestMethod]
    public void APhraseOfOpensOnlyWithFretsKeepsThem()
    {
        // No open is played alone, so nothing goes between an open and a fret: the opens are kept.
        var chart = Frets(Chord(0, Open, R), Chord(240, Open, R), Chord(480, Y));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Hard, Free(Hard)).Notes;

        Assert.AreSequenceEqual([Open, R, Open, R, Y], Lanes(notes));
    }

    [TestMethod]
    public void AnOpenWithFretsIsGreenPressedWithThem()
    {
        var chart = Frets(Chord(0, Open), Chord(240, R), Chord(480, Open, R), Chord(720, Open));

        Assert.AreSequenceEqual([G, Y, G, R, G], Lanes(Reducer.Reduce(chart, Guitar, Expert, Hard, Free(Hard)).Notes));
        Assert.AreSequenceEqual([G, Y, G, G], Lanes(Reducer.Reduce(chart, Guitar, Expert, Easy, Free(Easy)).Notes), "Easy keeps the chord's lowest, the green.");
    }

    [TestMethod]
    public void WithGreenTheHighestOpensAreKept()
    {
        // No fret can be lifted off green, so fretting the opens would leave nothing but green.
        var chart = Frets(Chord(0, R), Chord(240, Open), Chord(480, R), Chord(720, Open));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Free(Medium) with { HighestLane = 0 }).Notes;

        Assert.AreSequenceEqual([G, Open, G, Open], Lanes(notes));
    }

    [TestMethod]
    public void AFretBesideAnOpenOnlyOnceThinnedIsLiftedToo()
    {
        // Sixteenths open, yellow, green: thinned to eighths, green is beside the open, now green, so it's lifted off it,
        // not stretched over the range.
        var chart = Frets(Chord(0, Open), Chord(120, Y), Chord(240, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium) with { ThinningDivision = 8 }).Notes;

        Assert.AreSequenceEqual([G, R], Lanes(notes));
    }

    [TestMethod]
    public void AFloorIsRaisedToRatherThanStretchingThePhrase()
    {
        // Green beside a green open, then red: it fits, so green is raised to its floor, not spread over the range.
        Assert.AreSequenceEqual([1, 2], Reducer.FitPhrase([0, 1], [0, 0], 3, places: [0, 1], floors: [1, 0]).Select(p => p.Anchor).ToList());
        Assert.AreSequenceEqual([1], Reducer.FitPhrase([0], [0], 2, places: [0], floors: [1]).Select(p => p.Anchor).ToList(), "One note under its floor.");
    }

    [TestMethod]
    public void ARunTakesItsHighestFloor()
    {
        // Green twice, the second beside an open: placed as one, both are off green, whether the phrase fits or is scaled.
        Assert.AreSequenceEqual([1, 1], Reducer.FitPhrase([0, 0], [0, 0], 3, places: [0, 1], floors: [0, 1]).Select(p => p.Anchor).ToList());
        Assert.AreSequenceEqual([1, 1, 3], Reducer.FitPhrase([0, 0, 4], [0, 0, 0], 3, places: [0, 1, 4], floors: [0, 1, 0]).Select(p => p.Anchor).ToList());
    }

    [TestMethod]
    public void ForcingAndTapsAreKeptOnEveryDifficultyByDefault()
    {
        var chart = Frets(Chord(0, G), Chord(480, R), Chord(960, Y));
        Mark(chart, Guitar, Expert, 0, ChordFlags.ForceHopo);
        Mark(chart, Guitar, Expert, 480, ChordFlags.Tap);
        Mark(chart, Guitar, Expert, 960, ChordFlags.ForceStrum);

        foreach (var target in new[] { Hard, Medium, Easy })
            Assert.HasCount(3, Reducer.Reduce(chart, Guitar, Expert, target, Rules(target)).Flags, $"{target}");
    }

    [TestMethod]
    public void WithoutKeepingThemForcingAndTapsAreRemovedButAForcedStrumStays()
    {
        var chart = Frets(Chord(0, G), Chord(480, R), Chord(960, Y));
        Mark(chart, Guitar, Expert, 0, ChordFlags.ForceHopo);
        Mark(chart, Guitar, Expert, 480, ChordFlags.Tap);
        Mark(chart, Guitar, Expert, 960, ChordFlags.ForceStrum);

        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium) with { KeepForcedHopoAndTap = false }).Flags;

        Assert.AreSequenceEqual([new FlaggedChord(960, ChordFlags.ForceStrum)], medium.ToList());
    }

    [TestMethod]
    public void ANaturalHopoTheThinningWouldStrumIsForced()
    {
        // Sixteenths green, red, yellow, red: natural HOPOs after the first. On Medium the yellow, on the "and", is past the
        // HOPO threshold of green, so it's forced to stay a HOPO.
        var chart = Frets(Chord(0, G), Chord(120, R), Chord(240, Y), Chord(360, R), Chord(1920, G));

        var result = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium));

        Assert.AreSequenceEqual([0L, 240L, 1920L], result.Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([new FlaggedChord(240, ChordFlags.ForceHopo)], result.Flags.ToList());
    }

    [TestMethod]
    public void AHopoOnTheSameFretsAsTheChordBeforeIsAStrum()
    {
        // Yellow, then yellow forced to a HOPO: kept as played, it would be a HOPO on the note just played, which is
        // awkward, so each difficulty makes it a strum.
        var chart = Frets(Chord(0, Y), Chord(480, Y), Chord(1920, G));
        Mark(chart, Guitar, Expert, 480, ChordFlags.ForceHopo);

        foreach (var target in new[] { Hard, Medium, Easy })
        {
            var result = Reducer.Reduce(chart, Guitar, Expert, target, Rules(target));
            var read = GameView.Frets(result.Notes.ToList(), result.Flags, 480, ChartReadOptions.Default, []);

            Assert.IsFalse(read.Single(n => n.Tick == 480).Flags.HasFlag(NoteFlags.Hopo), $"{target}");
            Assert.DoesNotContain(f => f.Tick == 480, result.Flags, $"{target}: no forcing is left on it.");
        }
    }

    [TestMethod]
    public void ATapOnTheSameFretsAsTheChordBeforeStaysATap()
    {
        var chart = Frets(Chord(0, Y), Chord(480, Y), Chord(1920, G));
        Mark(chart, Guitar, Expert, 480, ChordFlags.Tap);

        var result = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium));

        Assert.Contains(new FlaggedChord(480, ChordFlags.Tap), result.Flags);
    }

    [TestMethod]
    public void AStrumTheThinningWouldMakeAHopoIsForced()
    {
        // Green, then red twice a thirty-second apart: the second red, after a red, is strummed. Hard leaves out the first
        // red, which puts the second a sixteenth after green, a natural HOPO, so it's forced to stay a strum.
        var chart = Frets(Chord(0, G), Chord(60, R), Chord(120, R), Chord(1920, G));

        var result = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard));

        Assert.AreSequenceEqual([0L, 120L, 1920L], result.Notes.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([new FlaggedChord(120, ChordFlags.ForceStrum)], result.Flags.ToList());
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
    [DataRow(60)]
    [DataRow(154)]
    [DataRow(240)]
    public void ThinningIsByFractionsOfABeatWhateverTheTempo(int bpm)
    {
        // Eighths and a sixteenth run: Medium keeps every eighth and Easy every beat, at a slow tempo or a fast one.
        var chart = Frets(bpm, [.. Enumerable.Range(0, 16).Select(i => Chord(i * 240L, i % 2 == 0 ? G : R)), .. Enumerable.Range(0, 16).Select(i => Chord(3840 + (i * 120L), i % 2 == 0 ? G : R))]);

        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Bursts(Medium)).Notes;
        var easy = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([.. Enumerable.Range(0, 24).Select(i => i * 240L)], medium.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([.. Enumerable.Range(0, 12).Select(i => i * 480L)], easy.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ATempoChangeLeavesTheThinningAsItIs()
    {
        // 120 BPM for a measure, then 240: Easy keeps each beat on both sides of the change.
        var chart = Frets(120, [.. Enumerable.Range(0, 16).Select(i => Chord(i * 240L, G))]);
        new AddTempo(new TempoChange(1920, 250_000)).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([.. Enumerable.Range(0, 8).Select(i => i * 480L)], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ABurstWithRoomAroundItIsKeptWhole()
    {
        // Three eighths, then a rest: Easy's gap alone kept the first and last; as a burst of three, all of them stay.
        var chart = Frets(Chord(0, G), Chord(240, R), Chord(480, Y), Chord(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 240L, 480L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ABurstOfFourIsCutToThree()
    {
        // Four eighths, then a rest: any four chords in a row span three beats, so one goes; the "and" of 2, before an empty
        // beat 3, is kept over the "and" of 1.
        var chart = Frets(Chord(0, G), Chord(240, R), Chord(480, Y), Chord(720, R), Chord(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 480L, 720L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ARunOfSixteenthsStillThinsToThePulse()
    {
        // Two measures of sixteenths: with bursts allowed, Medium still keeps the eighths and Easy the beats.
        var chart = Frets([.. Enumerable.Range(0, 32).Select(i => Chord(i * 120L, i % 2 == 0 ? G : R))]);

        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Bursts(Medium)).Notes;
        var easy = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([.. Enumerable.Range(0, 16).Select(i => i * 240L)], medium.Select(n => n.Tick).ToList());
        Assert.AreSequenceEqual([.. Enumerable.Range(0, 8).Select(i => i * 480L)], easy.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ASteadyStreamIsNoBurst()
    {
        // Eighths for two measures: as bursts, Easy would cut them into groups of three the chart never had. They're a
        // stream, longer than a burst, so Easy keeps the beats.
        var chart = Frets([.. Enumerable.Range(0, 16).Select(i => Chord(i * 240L, G))]);

        var easy = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([.. Enumerable.Range(0, 8).Select(i => i * 480L)], easy.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ABurstIsKeptWholeAtAFastTempo()
    {
        // At 240 BPM an eighth is 125 ms: the burst's grid is a fraction of a beat, so the three eighths stay.
        var chart = Frets(240, Chord(0, G), Chord(240, R), Chord(480, Y), Chord(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 240L, 480L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void ASustainABurstCutsShorterThanABeatIsAPlainNoteOnEasy()
    {
        // A green sustain of two beats, then green an eighth later in a burst: cut to an eighth, it's a plain note on Easy.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 960, G), Note(240, G), Note(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 240L, 1920L], notes.Select(n => n.Tick).ToList());
        Assert.AreEqual(EditableNote.PlainLength, notes[0].Length);
    }

    [TestMethod]
    public void ASustainHeldWhileOtherNotesArePlayedIsAPlainNote()
    {
        // Green held for a measure while red and yellow are played: each difficulty makes it plain, unless told to keep it.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 1920, G), Note(480, R), Note(960, Y), Note(3840, G));

        foreach (var target in new[] { Hard, Medium, Easy })
            Assert.AreEqual(EditableNote.PlainLength, Reducer.Reduce(chart, Guitar, Expert, target, Rules(target)).Notes.Single(n => n.Tick == 0).Length, $"{target}");
        var kept = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard) with { KeepExtendedSustains = true }).Notes;
        Assert.AreEqual(1920, kept.Single(n => n.Tick == 0).Length);
    }

    [TestMethod]
    public void ATapHeldWhileOtherNotesArePlayedStaysATap()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 1920, G), Note(480, R), Note(3840, G));
        Mark(chart, Guitar, Expert, 0, ChordFlags.Tap);

        var result = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium));

        Assert.AreEqual(EditableNote.PlainLength, result.Notes.Single(n => n.Tick == 0).Length);
        Assert.Contains(new FlaggedChord(0, ChordFlags.Tap), result.Flags);
    }

    [TestMethod]
    public void ASustainWhoseNotesUnderItAreLeftOutStaysASustain()
    {
        // Green held for two beats over a red sixteenth: Easy leaves the red out, so nothing is played under green.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 960, G), Note(120, R), Note(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 1920L], notes.Select(n => n.Tick).ToList());
        Assert.AreEqual(960, notes[0].Length);
    }

    [TestMethod]
    public void ASustainEndingAsTheNextChordStartsIsKept()
    {
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, new EditableNote(0, 960, G), Note(960, R), Note(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium)).Notes;

        Assert.AreEqual(960, notes.Single(n => n.Tick == 0).Length);
    }

    [TestMethod]
    public void ABurstOfOneKeepsEveryTwoChordsTheGapApart()
    {
        var chart = Frets(Chord(0, G), Chord(240, R), Chord(480, Y), Chord(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Bursts(Easy) with { MaxBurst = 1 }).Notes;

        Assert.AreSequenceEqual([0L, 480L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    [DataRow(1680L, 1920L, 0)]
    [DataRow(480L, 1920L, 0)]
    [DataRow(240L, 480L, 1)]
    [DataRow(120L, 240L, 2)]
    [DataRow(360L, 480L, 1)]
    [DataRow(160L, 240L, 2)]
    public void TheNextStrongerPositionIsTheBeatAnOffbeatLeadsTo(long tick, long next, int strength)
    {
        var map = NewChart(Guitar).TempoMap.ToTempoMap();

        Assert.AreEqual((next, strength), Reducer.NextStronger(tick, map));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1920L)]
    [DataRow(7L)]
    public void AMeasuresFirstBeatOrATickOffTheGridLeadsNowhere(long tick)
    {
        var map = NewChart(Guitar).TempoMap.ToTempoMap();

        Assert.IsNull(Reducer.NextStronger(tick, map));
    }

    [TestMethod]
    public void TheNextStrongerPositionFollowsTheTimeSignature()
    {
        // In 3/4 the "and" of beat 3 leads to the next measure's first beat, where in 4/4 it would lead to beat 4.
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 3, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.FourLane));
        var map = chart.TempoMap.ToTempoMap();

        Assert.AreEqual((1440L, 0), Reducer.NextStronger(1200, map));
    }

    [TestMethod]
    public void AnAndTiedOverAnEmptyDownbeatIsKeptOverBeatFour()
    {
        // Beats, then beat 4 and its "and" with nothing on the next downbeat: the "and" anticipates the downbeat, and is
        // kept in place of beat 4 a quarter's gap would leave out one of.
        var chart = Frets(Chord(0, G), Chord(480, R), Chord(960, Y), Chord(1440, R), Chord(1680, G), Chord(2400, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 480L, 960L, 1680L, 2400L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void AnOffbeatInARunAnticipatesNothing()
    {
        // The "and" of 1 with beat 2 charted after it scores as an eighth; with beat 2 empty, as nearly a beat.
        var map = NewChart(Guitar).TempoMap.ToTempoMap();
        double ScoreAt240(long[] ticks)
        {
            var chords = ticks.Select(t => (t, new[] { Note(t, G) })).ToList();
            var scores = Reducer.Scores(chords, [(0, chords.Count - 1)], null, map, 480, static (_, _) => false);
            return scores[Array.IndexOf(ticks, 240L)];
        }

        Assert.AreEqual(-2, ScoreAt240([0, 240, 480, 720, 960]));
        Assert.AreEqual(-(1 + ThinningWeights.Anticipation), ScoreAt240([0, 240, 720, 960]));
    }

    [TestMethod]
    public void AWeakNoteBeforeALongRestAnticipatesNothing()
    {
        // A sixteenth on the "a" of 4, the next downbeat empty: with the part going on in the next measure it anticipates
        // the downbeat; with two measures' rest after it, it's only a sixteenth.
        var map = NewChart(Guitar).TempoMap.ToTempoMap();
        double ScoreAt1800(long[] ticks)
        {
            var chords = ticks.Select(t => (t, new[] { Note(t, G) })).ToList();
            var scores = Reducer.Scores(chords, [(0, chords.Count - 1)], null, map, 480, static (_, _) => false);
            return scores[Array.IndexOf(ticks, 1800L)];
        }

        Assert.AreEqual(-ThinningWeights.Anticipation, ScoreAt1800([0, 1800, 2400, 2880]));
        Assert.AreEqual(-3, ScoreAt1800([0, 1800, 5760, 6240]));
    }

    [TestMethod]
    public void AChordNextToABurstKeepsThePaceWhicheverIsTakenFirst()
    {
        // Easy's spacing, a burst of three eighths and a chord a quarter after it: any four in a row span three quarters,
        // whether the burst or the chord after it scores better.
        var spacing = new ThinningSpacing(480, 240, 3);
        long[] ticks = [0, 240, 480, 960];

        foreach (double[] scores in new double[][] { [5, 5, 5, 0], [0, 0, 0, 5] })
        {
            var kept = Reducer.Thin(ticks, scores, spacing, [], []).Select(i => ticks[i]).ToList();

            for (int i = spacing.Burst; i < kept.Count; i++)
                Assert.IsGreaterThanOrEqualTo(spacing.Pace * spacing.Burst, kept[i] - kept[i - spacing.Burst], $"Scores {string.Join(",", scores)}: {string.Join(" ", kept)}.");
            Assert.HasCount(3, kept, $"Scores {string.Join(",", scores)}: {string.Join(" ", kept)}.");
        }
    }

    [TestMethod]
    public void AChordARiffLeftOutHadKeptTooNearIsKeptAfterAll()
    {
        // A riff's chord kept in one measure of three, and too near a chord of that measure to keep both: made the same as
        // the other two, the riff's chord is left out, and the chord beside it is kept in its place.
        long[] ticks = [240, 480, 2160, 4080];
        var riff = new Riff(1, [0, 2, 3]);

        var kept = Reducer.Thin(ticks, [1, 0, 1, 1], ThinningSpacing.Even(480), [], [1920, 3840], [riff]);

        Assert.AreSequenceEqual([1], kept);
    }

    [TestMethod]
    public void ARepeatedRiffIsThinnedTheSameEachTime()
    {
        // A riff of sixteenths and eighths four times over, in one phrase: its first and last measures have the phrase's
        // ends, but each is thinned the same.
        (long Offset, int Lane)[] riff = [(0, G), (240, R), (360, Y), (720, G), (960, R), (1200, Y), (1320, R), (1680, G)];
        var chart = Frets([.. Enumerable.Range(0, 4).SelectMany(m => riff.Select(r => Chord((m * 1920L) + r.Offset, r.Lane))), Chord(7680, G)]);

        foreach (var target in new[] { Medium, Easy })
        {
            var notes = Reducer.Reduce(chart, Guitar, Expert, target, Bursts(target)).Notes;
            var measures = Enumerable.Range(0, 4)
                .Select(m => string.Join(" ", notes.Where(n => n.Tick / 1920 == m).Select(n => $"{n.Tick % 1920}:{n.Lane}")))
                .ToList();

            Assert.HasCount(1, measures.Distinct(), $"{target}: {string.Join(" | ", measures)}");
        }
    }

    [TestMethod]
    public void OnDrumsAKickBeforeAnEmptyBeatIsKept()
    {
        // Kick, snare, kick, snare, then a kick on the "and" of 4 with nothing on the downbeat: the kick is kept, the
        // snare on beat 4 left out.
        var chart = DrumsChart(DrumsVariant.FourLane);
        Add(chart, Drums, Expert, Note(0, 0), Note(480, 1), Note(960, 0), Note(1440, 1), Note(1680, 0), Note(2400, 1));

        var notes = Reducer.Reduce(chart, Expert, Easy, DrumRules(Easy), DrumsVariant.FourLane).Notes;

        Assert.AreSequenceEqual([0L, 480L, 960L, 1680L, 2400L], notes.Select(n => n.Tick).ToList());
        Assert.AreEqual(0, notes.Single(n => n.Tick == 1680).Lane);
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
    public void AChordChangeOverTheSameLowestFretStillMovesASingleNote()
    {
        // Green–red twice, green–yellow twice, then green–red: Easy keeps one note a chord, the lowest, which was green each
        // time. The top note rises and falls, so Easy goes green, red, green.
        var chart = Frets(Chord(0, G, R), Chord(480, G, R), Chord(960, G, Y), Chord(1440, G, Y), Chord(1920, G, R));

        var easy = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;
        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium)).Notes;

        Assert.AreSequenceEqual([G, G, R, R, G], Lanes(easy));
        Assert.AreSequenceEqual([G, R, G, R, G, Y, G, Y, G, R], Lanes(medium), "Medium keeps the chords, which show the change.");
    }

    [TestMethod]
    public void AChordAsWideAsTheRangeIsNarrowedToKeepAStep()
    {
        // Green–blue, red–orange, red–blue, green–yellow, as Hard has them: on Medium (green to blue) red–orange has nowhere
        // to go as wide as it is, and was green–blue again. Narrowed, each change shows: up, down, down.
        var chart = Frets(Chord(0, G, B), Chord(240, G, B), Chord(480, R, O), Chord(720, R, O), Chord(960, R, B), Chord(1200, R, B), Chord(1440, G, Y), Chord(1680, G, Y));

        var medium = Reducer.Reduce(chart, Guitar, Expert, Medium, Rules(Medium)).Notes;

        Assert.AreSequenceEqual([G, B, G, B, R, B, R, B, R, Y, R, Y, G, Y, G, Y], Lanes(medium));
    }

    [TestMethod]
    public void ARunOfOneChordCountsAsOneStep()
    {
        // However long the run of red–orange, it's narrowed the same, rather than losing its step to stay wide.
        foreach (int run in new[] { 1, 16 })
        {
            int[] anchors = [0, .. Enumerable.Repeat(1, run), 0];
            int[] widths = [3, .. Enumerable.Repeat(3, run), 2];
            int[] tops = [3, .. Enumerable.Repeat(4, run), 2];

            var fitted = Reducer.FitPhrase(anchors, widths, 3, tops);

            Assert.AreEqual((1, 2), fitted[1], $"A run of {run}.");
            Assert.IsTrue(fitted.Skip(1).Take(run).All(p => p == (1, 2)), $"A run of {run} is placed the same throughout.");
        }
    }

    [TestMethod]
    public void AChordChangeAlreadyShownDoesNotMove()
    {
        // As chords of two, green–red to green–yellow is a step up as it is; as single notes it would be none.
        Assert.AreSequenceEqual([0, 0], Reducer.FitPhrase([0, 0], [1, 2], 3, [1, 2]).Select(p => p.Anchor).ToList());
        Assert.AreSequenceEqual([0, 1], Reducer.FitPhrase([0, 0], [0, 0], 2, [1, 2]).Select(p => p.Anchor).ToList());
        Assert.AreSequenceEqual([0, 0], Reducer.FitPhrase([0, 0], [0, 0], 2).Select(p => p.Anchor).ToList(), "Without the tops, single notes on one fret stay.");
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
        var rules = FretReductionRules.Default(ChartInstrumentKind.SixFret, Easy) with { ThinningDivision = 0 };

        var notes = Reducer.Reduce(chart, ChartInstrument.GhlGuitar, Expert, Easy, rules).Notes;

        Assert.AreSequenceEqual([0, 4], Lanes(notes));
    }

    [TestMethod]
    public void ALongSustainOnTheAndBeatsAShortNoteOnTheBeat()
    {
        // Ranked by position alone, the plain green on beat 2 took the place of the red sustain an eighth after it.
        var chart = NewChart(Guitar);
        Add(chart, Guitar, Expert, Note(0, G), Note(480, G), new EditableNote(720, 960, R), Note(1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([0L, 720L, 1920L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void AChordBeatsASingleNoteOnAsStrongABeat()
    {
        // Sixteenths a quarter's grid apart: the chord is kept over the green before it.
        var chart = Frets(Chord(600, G), Chord(1080, G), Chord(1320, G, Y), Chord(1800, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

        Assert.AreSequenceEqual([600L, 1320L, 1800L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void TheFirstNoteAfterARestIsKept()
    {
        // A rest after the first green, then red on beat 4 and yellow on the next measure: red is kept, as it starts a phrase.
        var chart = Frets(Chord(0, G), Chord(1440, R), Chord(1920, Y), Chord(2880, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { ThinningDivision = 2 }).Notes;

        Assert.AreSequenceEqual([0L, 1440L, 2880L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void WhereTheMelodyTurnsIsKept()
    {
        // Sixteenths green, red, yellow, red: yellow, where it turns, is kept over the red before it.
        var chart = Frets(Chord(600, G), Chord(1080, R), Chord(1320, Y), Chord(1800, R));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard) with { ThinningDivision = 4 }).Notes;

        Assert.AreSequenceEqual([600L, 1320L, 1800L], notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void APhrasesLastNoteIsKept()
    {
        // Sixteenths a quarter's grid apart before a rest: the second, which ends the phrase, is kept.
        var chart = Frets(Chord(600, G), Chord(1080, R), Chord(1320, Y), Chord(3000, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy)).Notes;

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
    public void AStarPowerPhraseWhoseBestChordIsTooNearTheTargetKeepsAnother()
    {
        // Easy's note at 900 leaves no room for the phrase's chord at 960, and ranked by score alone 1440 took the place of
        // the one at 1400.
        var chart = Frets(Chord(960, G), Chord(1400, R), Chord(1440, G), Chord(1920, G));
        Add(chart, Guitar, Easy, Note(900, G));
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 960, 460)).Apply(chart);

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy), (960, 2880)).Notes;

        Assert.Contains(n => n.Tick == 1400, notes);
        AssertGapsAtLeast(480, notes);
    }

    [TestMethod]
    public void OnlyARestCountsTowardsTheFirstChordAfterIt()
    {
        // The chord at 960 starts a phrase both times: after a section, with eighths running up to it, and after a rest.
        var chart = Frets(Chord(0, G));
        var map = chart.TempoMap.ToTempoMap();
        double ScoreAt960(long[] ticks, long[] sections)
        {
            var chords = ticks.Select(t => (t, new[] { Note(t, G) })).ToList();
            var scores = Reducer.Scores(chords, Reducer.Phrases(chords, sections, 480), null, map, 480, static (_, _) => false);
            return scores[Array.IndexOf(ticks, 960L)];
        }

        double afterSection = ScoreAt960([0, 240, 480, 720, 960], [960]);
        double afterRest = ScoreAt960([0, 240, 960], []);

        Assert.AreEqual(ThinningWeights.AfterRest, afterRest - afterSection);
    }

    [TestMethod]
    public void EveryStarPowerPhraseKeepsAHitOnDrums()
    {
        var chart = DrumsChart(DrumsVariant.FourLane);
        Add(chart, Drums, Expert, Note(0, 0), Note(0, 1), Note(120, 2), Note(480, 1), Note(960, 1));
        new AddPhrase(Drums, new Phrase(PhraseKind.StarPower, 100, 40)).Apply(chart);

        var notes = Reducer.Reduce(chart, Expert, Easy, DrumRules(Easy), DrumsVariant.FourLane).Notes;

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
    public void ATargetsSustainCutShortOutsideTheRangeStaysASustain()
    {
        // Easy with eighths and no short sustains: the target's sustain before the range is cut to an eighth by the draft's
        // first note, and stays a sustain, as the target outside the range is its own.
        var chart = Frets(Chord(240, G));
        Add(chart, Guitar, Easy, new EditableNote(0, 1920, G));

        var notes = Reducer.Reduce(chart, Guitar, Expert, Easy, Rules(Easy) with { ThinningDivision = 8 }, (200, 2000)).Notes;

        Assert.AreEqual(240, notes.First(n => n.Tick == 0).Length);
    }

    [TestMethod]
    public void TheTargetsFirstChordAfterTheRangeIsPlayedAsItWas()
    {
        // Hard's red at 960 was a strum after its green at 0; the draft's green at 840 now comes a sixteenth before it,
        // which would make it a natural HOPO, so it's forced to stay a strum.
        var chart = Frets(Chord(0, G), Chord(840, G));
        Add(chart, Guitar, Hard, Note(0, G), Note(960, R));

        var result = Reducer.Reduce(chart, Guitar, Expert, Hard, Rules(Hard), (0, 900));
        var read = GameView.Frets(result.Notes.ToList(), result.Flags, 480, ChartReadOptions.Default, []);

        Assert.IsFalse(read.Single(n => n.Tick == 960).Flags.HasFlag(NoteFlags.Hopo));
        Assert.Contains(new FlaggedChord(960, ChordFlags.ForceStrum), result.Flags);
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
    public void ARangeStartingInsideADiscoFlipTurnsItOnAtItsStart()
    {
        // Expert's flip starts before the range and ends inside it; Hard has no mixes.
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(0, 1), Note(480, 1), Note(960, 1), Note(1440, 1));
        new AddDiscoFlip(Expert, 0, 1200).Apply(chart);

        var result = Reducer.Reduce(chart, Expert, Hard, DrumReductionRules.Default(Hard), DrumsVariant.Pro, (960, 1920));

        Assert.AreSequenceEqual([new DrumMix(960, 0, DrumMix.DiscoFlipSetting), new DrumMix(1200, 0, "")], [.. result.Mixes]);
    }

    [TestMethod]
    public void ARangeEndingInsideADiscoFlipTurnsItOffAfterItsEnd()
    {
        // Expert's flip starts inside the range and runs past it; Hard has no mixes, so its notes after the range aren't flipped.
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(480, 1), Note(960, 1), Note(1440, 1), Note(2400, 1));
        new AddDiscoFlip(Expert, 960, 2400).Apply(chart);

        var result = Reducer.Reduce(chart, Expert, Hard, DrumReductionRules.Default(Hard), DrumsVariant.Pro, (480, 1920));

        Assert.AreSequenceEqual([new DrumMix(960, 0, DrumMix.DiscoFlipSetting), new DrumMix(1921, 0, "")], [.. result.Mixes]);
    }

    [TestMethod]
    public void ARangeLeavesTheTargetsMixesAloneWhenTheyAgree()
    {
        // Both have the same flip; the draft's mixes are the target's.
        var chart = DrumsChart(DrumsVariant.Pro);
        Add(chart, Drums, Expert, Note(0, 1), Note(960, 1), Note(1920, 1));
        Add(chart, Drums, Hard, Note(0, 1), Note(1920, 1));
        new AddDiscoFlip(Expert, 0, 2400).Apply(chart);
        new AddDiscoFlip(Hard, 0, 2400).Apply(chart);

        var result = Reducer.Reduce(chart, Expert, Hard, DrumReductionRules.Default(Hard), DrumsVariant.Pro, (480, 1440));

        Assert.AreSequenceEqual(chart.PartOf(Drums)![Hard].Mixes.ToList(), [.. result.Mixes]);
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
            FiveFretEasy = new FretReductionRules { MaxChordSize = 9, HighestLane = -2, ThinningDivision = 15, BurstDivision = 4, MaxBurst = 20 },
            SixFretEasy = new FretReductionRules { ThinningDivision = 4, BurstDivision = 8, MaxBurst = 0 },
            FiveFretMedium = new FretReductionRules { ThinningDivision = 8, KeepForcedHopoAndTap = false, BurstDivision = null, MaxBurst = null },
            SixFretHard = new FretReductionRules { MaxChordSize = 5, HighestLane = 4 },
            DrumsMedium = new DrumReductionRules { PadsPerHit = 0, ThinningDivision = -1, KickDivision = 9, KeepDoubleKick = true },
            FiveFretHard = null!,
        };

        var fixedUp = bad.Normalized();

        Assert.AreEqual(5, fixedUp.FiveFretEasy.MaxChordSize);
        Assert.AreEqual(0, fixedUp.FiveFretEasy.HighestLane);
        Assert.AreEqual(16, fixedUp.FiveFretEasy.ThinningDivision);
        Assert.AreEqual(16, fixedUp.FiveFretEasy.BurstDivision, "A burst's grid is no coarser than the thinning grid.");
        Assert.AreEqual(ReductionDefaults.MaxBurstLimit, fixedUp.FiveFretEasy.MaxBurst);
        Assert.AreEqual(8, fixedUp.SixFretEasy.BurstDivision);
        Assert.AreEqual(1, fixedUp.SixFretEasy.MaxBurst);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretMedium.BurstDivision, fixedUp.FiveFretMedium.BurstDivision);
        Assert.AreEqual(ReductionDefaults.Shipped.FiveFretMedium.MaxBurst, fixedUp.FiveFretMedium.MaxBurst);
        Assert.IsTrue(fixedUp.FiveFretMedium.KeepForcedHopoAndTap, "A set saved before bursts takes the shipped HOPOs and taps with them.");
        Assert.IsFalse(fixedUp.FiveFretMedium.KeepExtendedSustains, "A set saved before it takes the shipped.");
        Assert.IsFalse(fixedUp.FiveFretMedium.KeepOpenNotes, "A set saved before it takes the shipped.");
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
        Assert.AreSequenceEqual([16, 8, 4], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.ThinningDivision).ToList());
        foreach (var family in new[] { new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }, [shipped.SixFretHard, shipped.SixFretMedium, shipped.SixFretEasy] })
        {
            Assert.AreSequenceEqual([16, 16, 8], family.Select(r => r.BurstDivision ?? 0).ToList());
            Assert.AreSequenceEqual([3, 3, 3], family.Select(r => r.MaxBurst ?? 0).ToList());
        }

        Assert.AreSequenceEqual([16, 16, 8], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.BurstDivision ?? 0).ToList());
        Assert.AreSequenceEqual([3, 3, 3], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.MaxBurst ?? 0).ToList());
        Assert.AreSequenceEqual([true, true, true], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepForcedHopoAndTap).ToList());
        Assert.AreSequenceEqual([true, true, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepShortSustains).ToList());
        Assert.AreSequenceEqual([false, false, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepExtendedSustains ?? true).ToList());
        Assert.AreSequenceEqual([false, false, false], new[] { shipped.FiveFretHard, shipped.FiveFretMedium, shipped.FiveFretEasy }.Select(r => r.KeepOpenNotes ?? true).ToList());
        Assert.AreSequenceEqual([false, false, false], new[] { shipped.SixFretHard, shipped.SixFretMedium, shipped.SixFretEasy }.Select(r => r.KeepOpenNotes ?? true).ToList());
        Assert.AreSequenceEqual([2, 2, 1], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.PadsPerHit).ToList());
        Assert.AreSequenceEqual([0, 8, 4], new[] { shipped.DrumsHard, shipped.DrumsMedium, shipped.DrumsEasy }.Select(r => r.KickDivision).ToList());
    }

    // The shipped rules for target with no bursts, so each other rule is seen alone; the bursts have tests of their own.
    private static FretReductionRules Rules(ChartDifficulty target) => FretReductionRules.Default(ChartInstrumentKind.FiveFret, target) with { MaxBurst = 1 };

    private static DrumReductionRules DrumRules(ChartDifficulty target) => DrumReductionRules.Default(target) with { MaxBurst = 1 };

    // The shipped rules for target, bursts and all.
    private static FretReductionRules Bursts(ChartDifficulty target) => FretReductionRules.Default(ChartInstrumentKind.FiveFret, target);

    // The rules for target with no thinning, by grid or by time.
    private static FretReductionRules Free(ChartDifficulty target) => Rules(target) with { ThinningDivision = 0 };

    private static (long Tick, int[] Lanes) Chord(long tick, params int[] lanes) => (tick, lanes);

    private static EditableChart Frets(params (long Tick, int[] Lanes)[] chords) => Frets(120, chords);

    private static EditableChart Frets(int bpm, params (long Tick, int[] Lanes)[] chords)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, bpm, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.FourLane));
        Add(chart, Guitar, Expert, [.. chords.SelectMany(c => c.Lanes.Select(l => Note(c.Tick, l)))]);
        return chart;
    }

    // Two measures of eighths going between a fret and an open: R o R o G o G o, then R o R o Y o Y o.
    private static EditableChart OpensBetweenFrets()
    {
        int[] frets = [R, R, G, G, R, R, Y, Y];
        return Frets([.. Enumerable.Range(0, 16).Select(i => Chord(i * 240L, i % 2 == 0 ? frets[i / 2] : Open))]);
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
