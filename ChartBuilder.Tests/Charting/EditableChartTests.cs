using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class EditableChartTests
{
    private static EditableTrack Track(ChartInstrument instrument = ChartInstrument.Guitar, ChartDifficulty difficulty = ChartDifficulty.Expert) =>
        new EditablePart(instrument)[difficulty];

    [TestMethod]
    public void NotesStayInOrderThroughAddsAndRemoves()
    {
        var track = Track(ChartInstrument.Drums);
        var expected = new SortedSet<(long Tick, int Lane, bool DoubleKick)>();
        var random = new Random(11);

        for (int i = 0; i < 2000; i++)
        {
            long tick = random.Next(0, 50) * 120;
            int lane = random.Next(0, 6);
            bool doubleKick = lane == 0 && random.Next(2) == 0;
            if (random.Next(3) == 0)
            {
                bool removed = track.TryGetNote(tick, lane, doubleKick, out var note) && track.Notes.Remove(note);
                Assert.AreEqual(expected.Remove((tick, lane, doubleKick)), removed);
            }
            else
            {
                Assert.AreEqual(expected.Add((tick, lane, doubleKick)), track.Notes.Add(new EditableNote(tick, random.Next(0, 3), lane, DoubleKick: doubleKick)));
            }
        }

        Assert.AreSequenceEqual(expected.ToList(), [.. track.Notes.Select(n => (n.Tick, n.Lane, n.DoubleKick))]);
    }

    [TestMethod]
    public void ANoteIsNotAddedWhereOneAlreadyIs()
    {
        var track = Track();
        track.Notes.Add(new EditableNote(480, 240, 2));

        Assert.IsFalse(track.Notes.Add(new EditableNote(480, 1, 2)));
        Assert.AreSequenceEqual([new EditableNote(480, 240, 2)], track.Notes.ToList());
    }

    [TestMethod]
    public void OpenNotesComeFirstAndTheKickBeforeThe2xKick()
    {
        var guitar = Track();
        guitar.Notes.Add(new EditableNote(0, 1, 1));
        guitar.Notes.Add(new EditableNote(0, 1, ChartNote.OpenLane));
        var drums = Track(ChartInstrument.Drums);
        drums.Notes.Add(new EditableNote(0, 1, 1));
        drums.Notes.Add(new EditableNote(0, 1, 0, DoubleKick: true));
        drums.Notes.Add(new EditableNote(0, 1, 0));

        Assert.AreSequenceEqual([ChartNote.OpenLane, 1], guitar.Notes.Select(n => n.Lane).ToList());
        Assert.AreSequenceEqual(
            [new EditableNote(0, 1, 0), new EditableNote(0, 1, 0, DoubleKick: true), new EditableNote(0, 1, 1)], drums.Notes.ToList());
    }

    [TestMethod]
    public void NotesAreFoundByTickRangeAndPlace()
    {
        var track = Track();
        foreach (var (tick, lane) in new (long, int)[] { (0, 1), (240, 1), (480, 3), (480, 1), (720, 1), (960, 1) })
            track.Notes.Add(new EditableNote(tick, 1, lane));

        Assert.AreSequenceEqual([240L, 480, 480, 720], track.Notes.Between(240, 720).ToArray().Select(n => n.Tick).ToList());
        Assert.HasCount(2, track.Notes.At(480));
        Assert.IsTrue(track.Notes.Between(721, 959).IsEmpty);
        Assert.AreEqual(2, track.Notes.FirstAtOrAfter(480));
        Assert.AreEqual(4, track.Notes.FirstAfter(480));
        Assert.IsTrue(track.TryGetNote(480, 3, false, out var note));
        Assert.AreEqual(new EditableNote(480, 1, 3), note);
        Assert.IsFalse(track.TryGetNote(480, 2, false, out _));
    }

    [TestMethod]
    public void NotesThatCantBeOnTheTrackAreRejected()
    {
        var guitar = Track();
        var ghl = Track(ChartInstrument.GhlGuitar);
        var hardDrums = Track(ChartInstrument.Drums, ChartDifficulty.Hard);
        var drums = Track(ChartInstrument.Drums);

        Assert.ThrowsExactly<ArgumentException>(() => guitar.Notes.Add(new EditableNote(0, 1, 5)));
        Assert.ThrowsExactly<ArgumentException>(() => guitar.Notes.Add(new EditableNote(0, 1, 1, NoteDynamics.Accent)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => guitar.Notes.Add(new EditableNote(-1, 1, 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => guitar.Notes.Add(new EditableNote(0, -1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => ghl.Notes.Add(new EditableNote(0, 1, 6)));
        Assert.ThrowsExactly<ArgumentException>(() => drums.Notes.Add(new EditableNote(0, 1, ChartNote.OpenLane)));
        Assert.ThrowsExactly<ArgumentException>(() => drums.Notes.Add(new EditableNote(0, 1, 1, DoubleKick: true)));
        Assert.ThrowsExactly<ArgumentException>(() => hardDrums.Notes.Add(new EditableNote(0, 1, 0, DoubleKick: true)));
        Assert.ThrowsExactly<ArgumentException>(() => drums.Notes.Add(new EditableNote(0, 1, 0, NoteDynamics.Accent)));
        Assert.ThrowsExactly<ArgumentException>(() => drums.Notes.Add(new EditableNote(0, 1, 0, NoteDynamics.Ghost, DoubleKick: true)));
        Assert.IsTrue(ghl.Notes.Add(new EditableNote(0, 1, 5)));
        Assert.IsTrue(guitar.Notes.Add(new EditableNote(0, 0, ChartNote.OpenLane)));
        Assert.IsTrue(drums.Notes.Add(new EditableNote(0, 1, 5, NoteDynamics.Ghost)));
    }

    [TestMethod]
    public void ChordFlagsAreHeldPerTick()
    {
        var track = Track();
        track.Notes.Add(new EditableNote(480, 1, 0));
        track.Notes.Add(new EditableNote(480, 1, 2));

        Assert.AreEqual(ChordFlags.None, track.SetFlags(480, ChordFlags.ForceHopo));
        Assert.AreEqual(ChordFlags.ForceHopo, track.SetFlags(480, ChordFlags.ForceHopo | ChordFlags.Tap));
        Assert.AreEqual(ChordFlags.None, track.SetFlags(960, ChordFlags.ForceStrum));

        Assert.AreEqual(ChordFlags.ForceHopo | ChordFlags.Tap, track.FlagsAt(480));
        Assert.AreEqual(ChordFlags.None, track.FlagsAt(240));
        Assert.AreSequenceEqual(
            [new FlaggedChord(480, ChordFlags.ForceHopo | ChordFlags.Tap), new FlaggedChord(960, ChordFlags.ForceStrum)], track.FlaggedChords.ToList());

        Assert.AreEqual(ChordFlags.ForceStrum, track.SetFlags(960, ChordFlags.None));
        Assert.AreSequenceEqual([480L], track.FlaggedChords.Select(c => c.Tick).ToList());
        Assert.ThrowsExactly<ArgumentException>(() => track.FlaggedChords.Add(new FlaggedChord(0, ChordFlags.None)));
    }

    [TestMethod]
    public void PhrasesStayInOrderAndOnlyRollsAndTrillsReachDown()
    {
        var part = new EditablePart(ChartInstrument.Drums);
        part.Phrases.Add(new Phrase(PhraseKind.Solo, 960, 480));
        part.Phrases.Add(new Phrase(PhraseKind.Trill, 0, 240, ChartDifficulty.Expert));
        part.Phrases.Add(new Phrase(PhraseKind.StarPower, 960, 960));

        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.Trill, 0, 240, ChartDifficulty.Expert), new Phrase(PhraseKind.StarPower, 960, 960), new Phrase(PhraseKind.Solo, 960, 480)],
            part.Phrases.ToList());
        Assert.ThrowsExactly<ArgumentException>(() => part.Phrases.Add(new Phrase(PhraseKind.Roll, 0, 240)));
        Assert.ThrowsExactly<ArgumentException>(() => part.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 240, ChartDifficulty.Hard)));
        Assert.ThrowsExactly<ArgumentException>(() => part.TomMarkers.Add(new TomMarker(1, 0, 240)));
    }

    [TestMethod]
    public void EventsAtOneTickKeepTheOrderTheyCameIn()
    {
        var chart = new EditableChart(480);
        chart.Events.Add(TextEvent.Of(480, "[b]"));
        chart.Events.Add(TextEvent.Of(0, "[c]"));
        chart.Events.Add(TextEvent.Of(480, "[a]"));
        chart.TempoTrackRaw.Add(new RawEvent(0, [0x80, 1, 0]));
        chart.TempoTrackRaw.Add(new RawEvent(0, [0x90, 1, 100]));

        Assert.AreSequenceEqual(["[c]", "[b]", "[a]"], chart.Events.Select(e => e.Text).ToList());
        Assert.AreSequenceEqual([(byte)0x80, (byte)0x90], chart.TempoTrackRaw.Select(e => e.Status).ToList());
    }

    [TestMethod]
    public void PartsAreKeptInInstrumentOrder()
    {
        var chart = new EditableChart(480);
        chart.AddPart(ChartInstrument.GhlGuitar);
        chart.AddPart(ChartInstrument.Guitar);
        var drums = chart.AddPart(ChartInstrument.Drums);

        Assert.AreSequenceEqual(
            [ChartInstrument.Guitar, ChartInstrument.Drums, ChartInstrument.GhlGuitar], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.AreSame(drums, chart.PartOf(ChartInstrument.Drums));
        Assert.IsNull(chart.PartOf(ChartInstrument.Bass));
        Assert.ThrowsExactly<InvalidOperationException>(() => chart.AddPart(ChartInstrument.Drums));

        Assert.AreSame(drums, chart.RemovePart(ChartInstrument.Drums));
        Assert.IsNull(chart.RemovePart(ChartInstrument.Drums));
        chart.AddPart(drums);
        Assert.AreSame(drums, chart.Parts[1]);
    }

    [TestMethod]
    public void EachPartHasATrackPerDifficultyOfItsKind()
    {
        var part = new EditablePart(ChartInstrument.GhlBass);

        Assert.AreEqual(ChartInstrumentKind.SixFret, part.Kind);
        Assert.AreSequenceEqual(Enum.GetValues<ChartDifficulty>(), part.Tracks.Select(t => t.Difficulty).ToList());
        Assert.IsTrue(part.Tracks.All(t => t.Kind == ChartInstrumentKind.SixFret));
        Assert.AreSame(part.Tracks[3], part[ChartDifficulty.Expert]);
    }

    [TestMethod]
    public void ChangingACloneLeavesTheOriginalAsItWas()
    {
        foreach (var change in EditableChartSamples.Changes)
        {
            var original = EditableChartSamples.Full();
            var clone = original.Clone();

            change.Apply(clone);

            Assert.IsEmpty(ChartModelComparer.Differences(EditableChartSamples.Full(), original), change.Name);
            Assert.IsNotEmpty(ChartModelComparer.Differences(original, clone), change.Name);
        }
    }

    [TestMethod]
    public void ChangingTheOriginalLeavesACloneAsItWas()
    {
        foreach (var change in EditableChartSamples.Changes)
        {
            var original = EditableChartSamples.Full();
            var clone = original.Clone();
            _ = original.TempoMap.ToTempoMap();

            change.Apply(original);

            Assert.IsEmpty(ChartModelComparer.Differences(EditableChartSamples.Full(), clone), change.Name);
        }
    }

    [TestMethod]
    public void ACloneHasItsOwnTempoMap()
    {
        var original = EditableChartSamples.Full();
        var before = original.TempoMap.ToTempoMap();
        var clone = original.Clone();

        clone.TempoMap.Tempos.Add(new TempoChange(960, 250_000));

        Assert.AreSame(before, original.TempoMap.ToTempoMap());
        Assert.IsFalse(clone.TempoMap.ToTempoMap().SameAs(before));
    }

    [TestMethod]
    public void ACloneSharesTheBytesNothingChanges()
    {
        var original = EditableChartSamples.Full();

        var clone = original.Clone();

        Assert.AreSame(original.Passthrough[0], clone.Passthrough[0]);
        Assert.AreSame(original.TempoTrackRaw[0].Array, clone.TempoTrackRaw[0].Array);
    }

    [TestMethod]
    public void CreateNewBuildsWhatTheSettingsAskFor()
    {
        var settings = new NewChartSettings(
            "My Song", 960, 140, 6, 8, new HashSet<ChartInstrument> { ChartInstrument.Drums, ChartInstrument.Bass, ChartInstrument.GhlGuitar }, DrumsVariant.FiveLane);

        var chart = EditableChart.CreateNew(settings);

        Assert.AreEqual(960, chart.Resolution);
        Assert.AreEqual("My Song", chart.Name);
        Assert.AreSequenceEqual([new TempoChange(0, 428_571)], chart.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 6, 8, 24, 8)], chart.TempoMap.TimeSignatures.ToList());
        Assert.IsEmpty(chart.TempoMap.Anchors);
        Assert.AreSequenceEqual(
            [ChartInstrument.Bass, ChartInstrument.Drums, ChartInstrument.GhlGuitar], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.IsTrue(chart.Parts.All(p => p.Tracks.All(t => t.Notes.Count == 0) && p.Phrases.Count == 0));
        Assert.AreEqual(DrumsVariant.FiveLane, chart.DrumsVariant);
        Assert.IsEmpty(chart.Events, "The song events are placed as it's saved.");
        Assert.IsEmpty(chart.EventsRaw);
        Assert.IsEmpty(chart.TempoTrackRaw);
        Assert.IsEmpty(chart.Passthrough);
    }

    [TestMethod]
    public void CreateNewWithoutDrumsHasNoDrumsVariant()
    {
        var settings = new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro);

        Assert.IsNull(EditableChart.CreateNew(settings).DrumsVariant);
    }

    [TestMethod]
    public void CreateNewRejectsTimingMidiCantHold()
    {
        static NewChartSettings Settings(int resolution = 480, double bpm = 120, int numerator = 4, int denominator = 4) =>
            new("Song", resolution, bpm, numerator, denominator, new HashSet<ChartInstrument>(), DrumsVariant.Pro);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditableChart.CreateNew(Settings(resolution: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditableChart.CreateNew(Settings(bpm: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditableChart.CreateNew(Settings(numerator: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditableChart.CreateNew(Settings(denominator: 3)));
    }

    [TestMethod]
    [DataRow("[mix 3 drums0d]", ChartDifficulty.Expert, 0, "d", true)]
    [DataRow("[mix 0 drums2]", ChartDifficulty.Easy, 2, "", false)]
    [DataRow("[mix 2 drums4dnoflip]", ChartDifficulty.Hard, 4, "dnoflip", false)]
    [DataRow("[mix 1 drums1easy]", ChartDifficulty.Medium, 1, "easy", false)]
    public void MixEventsReadAndWriteTheSameTextAndAgreeWithTheGamesOnDiscoFlip(string text, ChartDifficulty difficulty, int configuration, string setting, bool discoFlip)
    {
        Assert.IsTrue(DrumMix.TryParse(text, 480, out var parsedDifficulty, out var mix));

        Assert.AreEqual(difficulty, parsedDifficulty);
        Assert.AreEqual(new DrumMix(480, configuration, setting), mix);
        Assert.AreEqual(discoFlip, mix.DiscoFlip);
        Assert.AreEqual(text, mix.TextFor(difficulty));
        Assert.IsTrue(ChartTextEvents.TryParseMix(ChartTextEvents.Normalize(text), out var gameDifficulty, out bool gameFlip));
        Assert.AreEqual(difficulty, gameDifficulty);
        Assert.AreEqual(discoFlip, gameFlip);
    }

    [TestMethod]
    [DataRow("mix 3 drums0d")]
    [DataRow("[mix_3_drums0d]")]
    [DataRow("[mix 3 drums0 d]")]
    [DataRow("[mix 4 drums0]")]
    [DataRow("[mix 3 drums6]")]
    [DataRow("[mix 3 drums]")]
    public void OtherSpellingsOfMixEventsAreLeftAsText(string text)
    {
        Assert.IsFalse(DrumMix.TryParse(text, 0, out _, out _));
    }
}
