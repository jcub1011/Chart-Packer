using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Difficulty;
using static ChartBuilder.Tests.Charting.DifficultyAnalysis.DifficultyCharts;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>What should hold however the model is tuned (docs/difficulty-model.md §10.3).</summary>
[TestClass]
public sealed class DifficultySanityTests
{
    private static readonly double[] Mixed = [0.25, 0.5, 1.0 / 3, 1, 0.25];

    [TestMethod]
    public void SpeedingAChartUpDoesNotLowerStrumNoteOrRhythm()
    {
        var notes = Varied(1, 24, 480, Mixed);
        var slow = Profile(Chart(notes, bpm: 150)).ChartAttributes;
        var fast = Profile(Chart(notes, bpm: 165)).ChartAttributes;

        Assert.IsGreaterThanOrEqualTo(slow.Strum, fast.Strum);
        Assert.IsGreaterThanOrEqualTo(slow.Note, fast.Note);
        Assert.IsGreaterThanOrEqualTo(slow.Rhythm, fast.Rhythm);
    }

    [TestMethod]
    [DataRow(DifficultyInstrument.FiveFret)]
    [DataRow(DifficultyInstrument.Keys)]
    [DataRow(DifficultyInstrument.SixFret)]
    [DataRow(DifficultyInstrument.Drums)]
    [DataRow(DifficultyInstrument.ProDrums)]
    public void SpeedingAnyInstrumentUpDoesNotLowerItsPhysicalAttributesOrScore(DifficultyInstrument instrument)
    {
        var p = DifficultyParameters.For(instrument);
        var notes = RandomNotes(instrument, 7, 400, 150);
        var slow = DifficultyAnalyzer.Analyze(notes, p);
        var fast = DifficultyAnalyzer.Analyze([.. notes.Select(n => n with { TimeMs = n.TimeMs / 1.1 })], p);

        Assert.IsGreaterThanOrEqualTo(slow.ChartAttributes.Strum, fast.ChartAttributes.Strum);
        Assert.IsGreaterThanOrEqualTo(slow.ChartAttributes.Note, fast.ChartAttributes.Note);
        Assert.IsGreaterThan(slow.ChartScore, fast.ChartScore);
    }

    [TestMethod]
    [DataRow(DifficultyInstrument.SixFret)]
    [DataRow(DifficultyInstrument.Drums)]
    [DataRow(DifficultyInstrument.ProDrums)]
    public void AnyInstrumentsEditChangesNothingBeforeIt(DifficultyInstrument instrument)
    {
        var p = DifficultyParameters.For(instrument);
        var notes = RandomNotes(instrument, 8, 200, 125);
        var edited = notes.Take(150).Concat(RandomNotes(instrument, 9, 50, 90).Select(n => n with { TimeMs = n.TimeMs + (150 * 125), Beat = n.Beat + 150 })).ToList();
        var before = DifficultyAnalyzer.Analyze(notes, p);
        var after = DifficultyAnalyzer.Analyze(edited, p);

        double editMs = 150 * 125;
        var unchanged = before.Samples.TakeWhile(s => s.TimeMs < editMs).ToList();
        Assert.IsNotEmpty(unchanged);
        Assert.AreSequenceEqual(unchanged, after.Samples.Take(unchanged.Count).ToList());
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    public void TheProViewIsNoEasierThanTheFourLaneView(int seed)
    {
        // The same drums, read with their cymbals and as 4-lane drums read them: each cymbal the tom of its colour.
        var pro = RandomNotes(DifficultyInstrument.ProDrums, seed, 400, 130);
        var fourLane = pro.Select(n => n with { Lanes = (byte)((n.Lanes & 0b1000_1111) | ((n.Lanes >> 3) & 0b1110)) }).ToList();

        var proScore = DifficultyAnalyzer.Analyze(pro, DifficultyParameters.For(DifficultyInstrument.ProDrums)).ChartScore;
        var fourLaneScore = DifficultyAnalyzer.Analyze(fourLane, DifficultyParameters.For(DifficultyInstrument.Drums)).ChartScore;

        Assert.IsGreaterThanOrEqualTo(fourLaneScore, proScore);
    }

    [TestMethod]
    public void AProChartWithoutCymbalsScoresAsItsFourLaneView()
    {
        var toms = RandomNotes(DifficultyInstrument.Drums, 14, 300, 140);

        Assert.AreEqual(
            DifficultyAnalyzer.Analyze(toms, DifficultyParameters.For(DifficultyInstrument.Drums)).ChartScore,
            DifficultyAnalyzer.Analyze(toms, DifficultyParameters.For(DifficultyInstrument.ProDrums)).ChartScore,
            1e-9);
    }

    [TestMethod]
    public void ALongEasyIntroAddsNoEnduranceAndHardlyMovesTheScore()
    {
        // Hard sixteenths at 120 BPM for about five minutes, longer than the K windows the score reads (§10.3), then the
        // same after a minute of whole notes: 120 beats, which is 150 windows, so the windows fall alike.
        var hard = Varied(2, 144, 480, [0.25, 0.25, 1.0 / 3]);
        var intro = Enumerable.Range(0, 30).Select(i => new EditableNote(i * 1920L, 1, i % 2));
        var plain = Profile(Chart(hard));
        var introduced = Profile(Chart(intro.Concat(hard.Select(n => n with { Tick = n.Tick + 57_600 }))));

        Assert.IsLessThanOrEqualTo(plain.ChartAttributes.Endurance * 1.001, introduced.ChartAttributes.Endurance);
        Assert.AreEqual(plain.ChartScore, introduced.ChartScore, 0.005 * plain.ChartScore);
    }

    [TestMethod]
    public void PlayingTheHardestSectionTwiceBuildsEndurance()
    {
        var hard = Enumerable.Range(0, 160).Select(i => new EditableNote(i * 120L, 1, i % 3 == 0 ? 0 : 1 + (i % 2))).ToList();
        var once = Profile(Chart(hard, bpm: 200)).ChartAttributes;
        var twice = Profile(Chart(hard.Concat(hard.Select(n => n with { Tick = n.Tick + (160 * 120) })), bpm: 200)).ChartAttributes;

        Assert.IsGreaterThan(0, once.Endurance);
        Assert.IsGreaterThan(once.Endurance, twice.Endurance);
        Assert.AreEqual(once.Note, twice.Note, 0.05 * once.Note);
    }

    [TestMethod]
    public void TheChartsResolutionChangesNothing()
    {
        var notes = Varied(3, 16, 480, Mixed);
        var low = Profile(Chart(notes, resolution: 480));
        var high = Profile(Chart(Scaled(notes, 2), resolution: 960));

        Assert.HasCount(low.Samples.Count, high.Samples);
        for (int i = 0; i < low.Samples.Count; i++)
        {
            Assert.AreEqual(low.Samples[i].TimeMs, high.Samples[i].TimeMs, 1e-6);
            Assert.AreEqual(low.Samples[i].Overall, high.Samples[i].Overall, 1e-6);
        }
    }

    [TestMethod]
    public void NotesMovedAFewTicksOffTheGridRateAsOnIt()
    {
        // Every note seven ticks late, as a chart synced by moving its notes rather than its tempo map.
        var notes = Varied(8, 24, 480, [0.5, 0.5, 1, 0.25]);
        var on = Profile(Chart(notes)).ChartAttributes;
        var off = Profile(Chart(notes.Select(n => n with { Tick = n.Tick + 7 }))).ChartAttributes;

        Assert.AreEqual(on.Rhythm, off.Rhythm, 0.01 * on.Rhythm);
        Assert.AreEqual(on.Pattern, off.Pattern, 0.01 * on.Pattern);
    }

    [TestMethod]
    public void ARolledChordRatesAsTheChord()
    {
        // Two-fret chords in eighths at 120 BPM, then the same with each chord's upper fret three ticks (3 ms) late.
        var chords = Enumerable.Range(0, 96).SelectMany(i => new[] { new EditableNote(i * 240L, 1, i % 3), new EditableNote(i * 240L, 1, (i % 3) + 2) }).ToList();
        var rolled = chords.Select((n, k) => k % 2 == 1 ? n with { Tick = n.Tick + 3 } : n);
        var plain = Profile(Chart(chords));
        var played = Profile(Chart(rolled));

        Assert.AreEqual(plain.ChartScore, played.ChartScore, 1e-9);
        Assert.AreEqual(plain.ChartAttributes.Rhythm, played.ChartAttributes.Rhythm, 1e-9);
        Assert.AreEqual(plain.ChartAttributes.Note, played.ChartAttributes.Note, 1e-9);
    }

    [TestMethod]
    public void ANoteJustAfterAChordIsPlayedWithItAndOneLaterIsNot()
    {
        // At 120 BPM a tick is about a millisecond: a strummed green, red three ticks later, then yellow thirty after.
        var notes = Notes(Chart([new EditableNote(0, 1, 0), new EditableNote(3, 1, 1), new EditableNote(33, 1, 2)]));

        Assert.HasCount(2, notes);
        Assert.AreEqual((byte)0b011, notes[0].Lanes);
        Assert.AreEqual(NoteKind.Strum, notes[0].Kind);
        Assert.AreEqual(0, notes[0].TimeMs, 1e-9);
        Assert.AreEqual((byte)0b100, notes[1].Lanes);
    }

    [TestMethod]
    public void TheGridOffsetIsFoundOnlyWhenTheNotesShareOne()
    {
        double tolerance = DifficultyParameters.Default.BeatTolerance;
        double[] onGrid = [.. Enumerable.Range(0, 64).Select(i => i * 0.5)];
        double[] late = [.. onGrid.Select(b => b + (7 / 480.0))];

        // Eighths on the off-beats only: a quarter-beat away, past what's looked for.
        double[] offBeats = [.. onGrid.Select(b => b + 0.25)];

        Assert.AreEqual(0, DifficultyNotes.GridOffset(onGrid, tolerance));
        Assert.AreEqual(7 / 480.0, DifficultyNotes.GridOffset(late, tolerance), 1e-9);
        Assert.AreEqual(0, DifficultyNotes.GridOffset(offBeats, tolerance));
    }

    [TestMethod]
    public void DoubleTimeAtTheSameNoteTimesKeepsStrumAndNote()
    {
        // Eighths and quarters: none close enough to be natural HOPOs at either tempo.
        var notes = Varied(4, 16, 480, [0.5, 1]);
        var plain = Profile(Chart(notes, bpm: 120)).ChartAttributes;
        var doubled = Profile(Chart(Scaled(notes, 2), bpm: 240)).ChartAttributes;

        Assert.AreEqual(plain.Strum, doubled.Strum, 1e-6);
        Assert.AreEqual(plain.Note, doubled.Note, 1e-6);
    }

    [TestMethod]
    public void StrummingAHopoRunRaisesStrum()
    {
        // Sixteenths at 160 BPM moving between frets: natural HOPOs.
        var run = Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, (i * 2) % 5)).ToList();
        var chart = Chart(run, bpm: 160);
        var hopos = Profile(chart).ChartAttributes;
        new SetChordFlags(Guitar, Expert, run.Select(n => n.Tick), ChordFlags.ForceStrum, on: true).Apply(chart);
        var strummed = Profile(chart).ChartAttributes;

        Assert.IsGreaterThan(hopos.Strum * 2, strummed.Strum);
        Assert.IsLessThan(hopos.Note, strummed.Note);
    }

    [TestMethod]
    public void TheProfilesValuesMatchReadingTheNotesOneByOne()
    {
        var notes = Notes(Chart(Varied(5, 24, 480, Mixed), bpm: 170));
        var profile = DifficultyAnalyzer.Analyze(notes);
        var random = new Random(5);
        double end = notes[^1].TimeMs;
        for (int probe = 0; probe < 50; probe++)
        {
            double at = random.NextDouble() * end;
            var state = new DifficultyState(DifficultyModel.For(DifficultyParameters.Default));
            foreach (var note in notes.TakeWhile(n => n.TimeMs <= at))
                state.Advance(note);
            state.AdvanceTo(at);

            var value = profile.ValueAt(at).Attributes;
            Assert.AreEqual(state.Levels.Strum, value.Strum, 1e-9 + (1e-9 * value.Strum));
            Assert.AreEqual(state.Levels.Note, value.Note, 1e-9 + (1e-9 * value.Note));
            Assert.AreEqual(state.Levels.Rhythm, value.Rhythm, 1e-9 + (1e-9 * value.Rhythm));
            Assert.AreEqual(state.Levels.Pattern, value.Pattern, 1e-9 + (1e-9 * value.Pattern));
            Assert.AreEqual(state.Levels.Endurance, value.Endurance, 1e-6 + (1e-3 * value.Endurance));
        }
    }

    [TestMethod]
    public void AnEditChangesNothingBeforeIt()
    {
        var notes = Varied(6, 24, 480, Mixed);
        var chart = Chart(notes);
        var before = Profile(chart);
        long editTick = notes[200].Tick;
        var edited = notes.Where(n => n.Tick < editTick).Concat(Varied(7, 8, 480, Mixed, editTick)).ToList();
        var after = Profile(Chart(edited));
        double editMs = chart.TempoMap.ToTempoMap().SecondsAt(editTick) * 1000;

        var earlier = before.Samples.Where(s => s.TimeMs < editMs).ToList();
        CollectionAssert.AreEqual(earlier, after.Samples.Take(earlier.Count).ToList());
        Assert.AreEqual(before.ValueAt(editMs - 10), after.ValueAt(editMs - 10));
    }

    [TestMethod]
    public void AStretchAloneIsEasierThanInTheSong()
    {
        // A hard minute, then the stretch scored: in the song it has the minute's build-up behind it.
        var notes = Notes(Chart(Enumerable.Range(0, 640).Select(i => new EditableNote(i * 120L, 1, (i * 3) % 5)), bpm: 200));
        var profile = DifficultyAnalyzer.Analyze(notes);
        var span = new DifficultySpan(notes[600].TimeMs, notes[^1].TimeMs + 1);
        var alone = DifficultyAnalyzer.Analyze(notes, span);

        Assert.IsGreaterThan(alone.Score(span.FromMs, span.ToMs), profile.Score(span.FromMs, span.ToMs));
        Assert.IsGreaterThan(alone.AttributeScores(span.FromMs, span.ToMs).Endurance, profile.AttributeScores(span.FromMs, span.ToMs).Endurance);
    }

    [TestMethod]
    public void SilenceBeforeTheFirstNoteChangesNoScore()
    {
        var notes = Notes(Chart(Varied(8, 24, 480, Mixed), bpm: 150));
        var plain = DifficultyAnalyzer.Analyze(notes);
        double silence = 50 * plain.Parameters.WindowMs;
        var late = DifficultyAnalyzer.Analyze([.. notes.Select(n => n with { TimeMs = n.TimeMs + silence })]);

        Assert.AreEqual(plain.ChartScore, late.ChartScore, 1e-9 * plain.ChartScore);
        Assert.AreEqual(plain.ChartAttributes.Strum, late.ChartAttributes.Strum, 1e-9 * plain.ChartAttributes.Strum);
        Assert.AreEqual(plain.ChartAttributes.Endurance, late.ChartAttributes.Endurance, 1e-6 + (1e-6 * plain.ChartAttributes.Endurance));
    }

    [TestMethod]
    public void AStretchAlonesChartScoreIsItsOwn()
    {
        var notes = Notes(Chart(Enumerable.Range(0, 640).Select(i => new EditableNote(i * 120L, 1, (i * 3) % 5)), bpm: 200));
        var span = new DifficultySpan(notes[600].TimeMs, notes[^1].TimeMs + 1);

        var alone = DifficultyAnalyzer.Analyze(notes, span);

        Assert.AreEqual(alone.Score(span.FromMs, span.ToMs), alone.ChartScore, 1e-9);
    }

    // Sixteenths intervalMs apart on lanes picked from seed, as instrument lays them out: frets with some chords, opens
    // and HOPOs; drums on the pads, Pro cymbals among them, with kicks.
    private static List<DifficultyNote> RandomNotes(DifficultyInstrument instrument, int seed, int count, double intervalMs)
    {
        var random = new Random(seed);
        var notes = new List<DifficultyNote>(count);
        for (int i = 0; i < count; i++)
        {
            byte lanes;
            var kind = NoteKind.Strum;
            if (instrument.IsDrums())
            {
                int pads = instrument == DifficultyInstrument.ProDrums ? 7 : 4;
                lanes = (byte)(1 << random.Next(pads));
                if (random.Next(4) == 0)
                    lanes |= (byte)(1 << random.Next(pads));
                if (random.Next(3) == 0)
                    lanes |= DrumPads.Kick;
            }
            else
            {
                int frets = instrument == DifficultyInstrument.SixFret ? 6 : 5;
                int roll = random.Next(10);
                lanes = roll == 0 ? (byte)0 : (byte)(1 << random.Next(frets));
                if (roll >= 8)
                    lanes |= (byte)(1 << random.Next(frets));
                if (random.Next(3) == 0)
                    kind = NoteKind.Hopo;
            }

            notes.Add(new DifficultyNote(i * intervalMs, i * 0.25, lanes, kind, 0));
        }

        return notes;
    }
}
