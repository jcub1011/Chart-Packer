using System.Globalization;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>What <see cref="MidiChartWriter"/> normalizes, and where it puts each event.</summary>
[TestClass]
public sealed class MidiChartWriterLayoutTests
{
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    // Normalizing.

    [TestMethod]
    public void ANoteHeldIntoTheNextOnItsPitchIsCutThere()
    {
        var chart = NewChart(ChartInstrument.Drums);
        var expert = chart.PartOf(ChartInstrument.Drums)![Expert];
        expert.Notes.Add(new EditableNote(0, 500, 1));
        expert.Notes.Add(new EditableNote(480, 0, 1)); // Its note-on would be ignored while the first is held.
        expert.Notes.Add(new EditableNote(480, 100, 2)); // Another lane.
        expert.Notes.Add(new EditableNote(960, 480, 0));
        expert.Notes.Add(new EditableNote(1200, 10, 0, DoubleKick: true)); // The 2x kick is another pitch.
        expert.Notes.Add(new EditableNote(1440, 1, 0));

        var saved = MidiChartWriter.Normalize(chart);

        Assert.AreSequenceEqual(
            [
                new EditableNote(0, 480, 1), new EditableNote(480, 0, 1), new EditableNote(480, 100, 2),
                new EditableNote(960, 480, 0), new EditableNote(1200, 10, 0, DoubleKick: true), new EditableNote(1440, 1, 0),
            ],
            saved.PartOf(ChartInstrument.Drums)![Expert].Notes.ToList());
        Assert.AreEqual(500, expert.Notes[0].Length, "The chart normalized is left as it was.");
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void OfAKickAndA2xKickOnATickTheOneLetGoFirstIsKept()
    {
        var chart = NewChart(ChartInstrument.Drums);
        var expert = chart.PartOf(ChartInstrument.Drums)![Expert];
        foreach (var (tick, kick, doubleKick) in ((long, long, long)[])[(0, 10, 20), (480, 20, 10), (960, 10, 10)])
        {
            expert.Notes.Add(new EditableNote(tick, kick, 0));
            expert.Notes.Add(new EditableNote(tick, doubleKick, 0, DoubleKick: true));
        }

        var saved = MidiChartWriter.Normalize(chart);

        Assert.AreSequenceEqual(
            [new EditableNote(0, 10, 0), new EditableNote(480, 10, 0, DoubleKick: true), new EditableNote(960, 10, 0, DoubleKick: true)],
            saved.PartOf(ChartInstrument.Drums)![Expert].Notes.ToList());
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void OverlappingPhrasesOnTheSamePitchesMerge()
    {
        var chart = NewChart(ChartInstrument.Drums);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[Expert].Notes.Add(new EditableNote(0, 1, 1));
        foreach (var phrase in (Phrase[])
        [
            new(PhraseKind.StarPower, 0, 960), new(PhraseKind.StarPower, 480, 960), // Overlapping.
            new(PhraseKind.StarPower, 1440, 480), // Starting as the last ends.
            new(PhraseKind.Solo, 0, 0), new(PhraseKind.Solo, 0, 480), // Starting together.
            new(PhraseKind.Roll, 1920, 480, Hard), new(PhraseKind.Roll, 2160, 480, ChartDifficulty.Medium), new(PhraseKind.Roll, 2160, 480, Expert),
            new(PhraseKind.Trill, 1920, 480, Expert), // Another pitch.
            new(PhraseKind.DrumFill, 3840, 480), new(PhraseKind.BigRockEnding, 4000, 480), // Sharing pitches.
        ])
        {
            drums.Phrases.Add(phrase);
        }

        var saved = MidiChartWriter.Normalize(chart);

        Assert.AreSequenceEqual(
            [
                new Phrase(PhraseKind.StarPower, 0, 1440),
                new Phrase(PhraseKind.Solo, 0, 480),
                new Phrase(PhraseKind.StarPower, 1440, 480),
                new Phrase(PhraseKind.Roll, 1920, 720, ChartDifficulty.Medium),
                new Phrase(PhraseKind.Trill, 1920, 480, Expert),
                new Phrase(PhraseKind.DrumFill, 3840, 640),
            ],
            saved.PartOf(ChartInstrument.Drums)!.Phrases.ToList());
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void OverlappingTomMarkersOnAPadMerge()
    {
        var chart = NewChart(ChartInstrument.Drums);
        chart.DrumsVariant = DrumsVariant.Pro;
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[Expert].Notes.Add(new EditableNote(0, 1, 2));
        drums[Expert].Notes.Add(new EditableNote(480, 1, 2));
        drums.TomMarkers.Add(new TomMarker(2, 0, 240));
        drums.TomMarkers.Add(new TomMarker(2, 0, 0));
        drums.TomMarkers.Add(new TomMarker(2, 120, 480));
        drums.TomMarkers.Add(new TomMarker(2, 600, 10));
        drums.TomMarkers.Add(new TomMarker(3, 120, 10));

        var saved = MidiChartWriter.Normalize(chart);

        Assert.AreSequenceEqual(
            [new TomMarker(2, 0, 600), new TomMarker(3, 120, 10), new TomMarker(2, 600, 10)],
            saved.PartOf(ChartInstrument.Drums)!.TomMarkers.ToList());

        // Merged, the marker makes the yellow at 480 a tom: as two, it would have been toggled back to a cymbal.
        var notes = GameView.Project(saved, ChartReadOptions.Default).Tracks[new ChartTrackKey(ChartInstrument.Drums, Expert)].Notes;
        Assert.IsTrue(notes.All(n => !n.Flags.HasFlag(NoteFlags.Cymbal)));
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void ChartDynamicsAndEnhancedOpensFollowTheNotes()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.GhlGuitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar[Hard].Notes.Add(new EditableNote(0, 1, ChartNote.OpenLane));
        var bass = chart.PartOf(ChartInstrument.Bass)!;
        bass.EnhancedOpens = true;
        var ghl = chart.PartOf(ChartInstrument.GhlGuitar)!;
        ghl.EnhancedOpens = true;
        ghl[Expert].Notes.Add(new EditableNote(0, 1, ChartNote.OpenLane));
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums.ChartDynamics = true;
        drums[Expert].Notes.Add(new EditableNote(0, 1, 1));

        var saved = MidiChartWriter.Normalize(chart);

        // An open needs [ENHANCED_OPENS]; a part that had it keeps it; six-fret opens don't use it.
        Assert.IsTrue(saved.PartOf(ChartInstrument.Guitar)!.EnhancedOpens);
        Assert.IsTrue(saved.PartOf(ChartInstrument.Bass)!.EnhancedOpens);
        Assert.IsFalse(saved.PartOf(ChartInstrument.GhlGuitar)!.EnhancedOpens);

        // [ENABLE_CHART_DYNAMICS] goes when no note is an accent or a ghost, and comes back with one.
        Assert.IsFalse(saved.PartOf(ChartInstrument.Drums)!.ChartDynamics);
        drums.ChartDynamics = false;
        drums[Hard].Notes.Add(new EditableNote(480, 1, 0, NoteDynamics.Accent));
        Assert.IsTrue(MidiChartWriter.Normalize(chart).PartOf(ChartInstrument.Drums)!.ChartDynamics);
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void AMarkingOnATickWithoutANoteIsLeftOut()
    {
        var chart = NewChart(ChartInstrument.Guitar);
        var expert = chart.PartOf(ChartInstrument.Guitar)![Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0));
        expert.SetFlags(0, ChordFlags.Tap);
        expert.SetFlags(480, ChordFlags.ForceHopo);
        chart.PartOf(ChartInstrument.Guitar)![Hard].SetFlags(0, ChordFlags.ForceStrum);

        var saved = MidiChartWriter.Normalize(chart).PartOf(ChartInstrument.Guitar)!;

        Assert.AreSequenceEqual([new FlaggedChord(0, ChordFlags.Tap)], saved[Expert].FlaggedChords.ToList());
        Assert.IsEmpty(saved[Hard].FlaggedChords);
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void TextEventsTheLoaderTakesForSettingsBecomeThem()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar[Expert].Notes.Add(new EditableNote(0, 1, 0));
        guitar.TextEvents.Add(TextEvent.Of(480, "[ENHANCED_OPENS]"));
        guitar.TextEvents.Add(new TextEvent(480, 0x03, "Not a name"));
        guitar.TextEvents.Add(TextEvent.Of(480, "[idle]"));
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[Expert].Notes.Add(new EditableNote(0, 1, 1, NoteDynamics.Accent));
        drums.TextEvents.Add(TextEvent.Of(960, "[mix 3 drums0d]"));
        drums.TextEvents.Add(TextEvent.Of(960, "ENABLE_CHART_DYNAMICS"));
        drums.TextEvents.Add(new TextEvent(960, 0x05, "[mix_1_drums0d]"));
        chart.Events.Add(new TextEvent(960, 0x7F, "x"));

        var saved = MidiChartWriter.Normalize(chart);

        var savedGuitar = saved.PartOf(ChartInstrument.Guitar)!;
        Assert.IsTrue(savedGuitar.EnhancedOpens);
        Assert.AreSequenceEqual([TextEvent.Of(480, "[idle]")], savedGuitar.TextEvents.ToList());
        Assert.AreSequenceEqual([new RawEvent(480, [0xFF, 0x03, 10, .. "Not a name"u8])], savedGuitar.RawEvents.ToList());
        var savedDrums = saved.PartOf(ChartInstrument.Drums)!;
        Assert.IsTrue(savedDrums.ChartDynamics);
        Assert.AreSequenceEqual([new DrumMix(960, 0, DrumMix.DiscoFlipSetting)], savedDrums[Expert].Mixes.ToList());
        Assert.AreSequenceEqual([new TextEvent(960, 0x05, "[mix_1_drums0d]")], savedDrums.TextEvents.ToList());
        Assert.IsEmpty(saved.Events.Where(e => e.Type == 0x7F));
        Assert.AreSequenceEqual([new RawEvent(960, [0xFF, 0x7F, 1, (byte)'x'])], saved.EventsRaw.ToList());
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void NormalizingANormalizedChartChangesNothing()
    {
        foreach (var fixture in MidiFixtures.All)
        {
            var saved = MidiChartWriter.Normalize(MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart);

            Assert.IsEmpty(ChartModelComparer.Differences(saved, MidiChartWriter.Normalize(saved)), fixture.Name);
        }

        var sample = MidiChartWriter.Normalize(EditableChartSamples.Full());
        Assert.IsEmpty(ChartModelComparer.Differences(sample, MidiChartWriter.Normalize(sample)));

        // The sample's kick and 2x kick on one tick, as long as each other: the 2x kick is let go first.
        Assert.IsFalse(sample.PartOf(ChartInstrument.Drums)![Expert].TryGetNote(0, 0, doubleKick: false, out _));
        // The sample's passthrough track is named as its bytes aren't, which a loaded one can't be.
        var full = EditableChartSamples.Full();
        full.SetPassthrough(0, new PassthroughTrack(EditableChartSamples.VocalsChunk, null));
        AssertWritesChecked(full);
    }

    [TestMethod]
    public void AChartWithoutANameIsWrittenWithAnEmptyOne()
    {
        var chart = new EditableChart(480);
        chart.TempoMap.Tempos.Add(new TempoChange(480, 600_000));
        Assert.IsNull(chart.Name);

        var saved = MidiChartWriter.Normalize(chart);

        // Named, the tempo track starts at tick 0, so its tempo stays at 480.
        Assert.AreEqual(string.Empty, saved.Name);
        var result = AssertWritesChecked(chart);
        Assert.AreEqual(480, ChartReader.Read(result.Bytes, "notes.mid").Tempo.Tempos[^1].Tick);
    }

    // Where each event goes.

    [TestMethod]
    public void APartTracksEventsFollowPartEventOrder()
    {
        var chart = NewChart(ChartInstrument.Drums);
        chart.DrumsVariant = DrumsVariant.FiveLane;
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var expert = drums[Expert];
        expert.Notes.Add(new EditableNote(0, 480, 1, NoteDynamics.Accent)); // Let go at 480.
        expert.Notes.Add(new EditableNote(480, 0, 1)); // On its pitch as the first is let go.
        expert.Notes.Add(new EditableNote(480, 10, 2));
        expert.Mixes.Add(new DrumMix(480, 0, DrumMix.DiscoFlipSetting));
        drums[ChartDifficulty.Easy].Mixes.Add(new DrumMix(480, 0, ""));
        drums.TextEvents.Add(TextEvent.Of(480, "[idle]"));
        drums.RawEvents.Add(new RawEvent(480, [0xB0, 7, 100]));
        expert.Notes.Add(new EditableNote(960, 1, 5));
        drums.TomMarkers.Add(new TomMarker(2, 960, 10));

        var events = EventsOf(MidiChartWriter.Write(chart), "PART DRUMS");

        Assert.AreSequenceEqual([(0L, "name PART DRUMS"), (0L, "text [ENABLE_CHART_DYNAMICS]"), (0L, "on 97 127")], events.Where(e => e.Tick == 0).ToList());
        Assert.AreSequenceEqual(
            [
                (480L, "off 97"),
                (480L, "text [mix 0 drums0]"), (480L, "text [mix 3 drums0d]"),
                (480L, "text [idle]"), (480L, "raw B00764"),
                (480L, "on 97 100"), (480L, "off 97"), (480L, "on 98 100"),
            ],
            events.Where(e => e.Tick == 480).ToList());

        // The fifth-pad note starts before the tom marker, and settles the layout as five-lane first.
        Assert.AreSequenceEqual([(960L, "on 101 100"), (960L, "on 110 100")], events.Where(e => e.Tick == 960).ToList());
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void OpensAndTapsAreWrittenAsTheLayoutSays()
    {
        var chart = NewChart(ChartInstrument.Guitar);
        var expert = chart.PartOf(ChartInstrument.Guitar)![Expert];
        expert.Notes.Add(new EditableNote(0, 0, ChartNote.OpenLane));
        expert.SetFlags(0, ChordFlags.Tap | ChordFlags.ForceHopo);
        expert.Notes.Add(new EditableNote(1, 0, 0));
        expert.SetFlags(1, ChordFlags.Tap | ChordFlags.ForceStrum);
        chart.PartOf(ChartInstrument.Guitar)![Hard].Notes.Add(new EditableNote(1, 0, 4));

        var events = EventsOf(MidiChartWriter.Write(chart), "PART GUITAR");

        // [ENHANCED_OPENS] before the open below green; a tap's sysex on after the note-ons, and off after the
        // note-offs a tick later; force markers a tick long, on Expert's 101 and 102.
        Assert.AreSequenceEqual(
            [
                (0L, "name PART GUITAR"), (0L, "text [ENHANCED_OPENS]"),
                (0L, "on 95 100"), (0L, "off 95"), (0L, "on 101 100"), (0L, "sysex 3 4 on"),
                (1L, "off 101"), (1L, "sysex 3 4 off"),
                (1L, "on 88 100"), (1L, "off 88"), (1L, "on 96 100"), (1L, "off 96"), (1L, "on 102 100"), (1L, "sysex 3 4 on"),
                (2L, "off 102"), (2L, "sysex 3 4 off"),
                (2L, "end"),
            ],
            events);
        AssertWritesChecked(chart);
    }

    [TestMethod]
    public void SixFretOpensAndDrumLanesAreWrittenAtTheirPitches()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.GhlBass, ChartInstrument.Drums }, DrumsVariant.Pro));
        var ghl = chart.PartOf(ChartInstrument.GhlBass)![Expert];
        ghl.Notes.Add(new EditableNote(0, 0, ChartNote.OpenLane));
        ghl.Notes.Add(new EditableNote(0, 0, 5));
        ghl.SetFlags(0, ChordFlags.ForceHopo);
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[Expert].Notes.Add(new EditableNote(0, 0, 0, DoubleKick: true));
        drums.Phrases.Add(new Phrase(PhraseKind.Roll, 0, 0, ChartDifficulty.Easy));
        drums.Phrases.Add(new Phrase(PhraseKind.Trill, 480, 0, Hard));
        drums.Phrases.Add(new Phrase(PhraseKind.DrumFill, 960, 0));
        byte[] midi = MidiChartWriter.Write(chart);

        Assert.AreSequenceEqual(
            ["on 94 100", "on 100 100", "on 101 100"],
            EventsOf(midi, "PART BASS GHL").Where(e => e.Event.StartsWith("on ", StringComparison.Ordinal)).Select(e => e.Event).ToList());
        Assert.AreSequenceEqual(
            ["on 95 100", "on 126 25", "on 127 45", "on 120 100", "on 121 100", "on 122 100", "on 123 100", "on 124 100"],
            EventsOf(midi, "PART DRUMS").Where(e => e.Event.StartsWith("on ", StringComparison.Ordinal)).Select(e => e.Event).ToList());
        AssertWritesChecked(chart);
    }

    // BEAT.

    [TestMethod]
    public void BeatHasDownbeatsAndBeatsThroughTheTimeSignatures()
    {
        var chart = NewChart(ChartInstrument.Guitar);
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(3840, 3, 8));
        chart.PartOf(ChartInstrument.Guitar)![Expert].Notes.Add(new EditableNote(4800, 0, 0));

        var events = EventsOf(MidiChartWriter.Write(chart), "BEAT");

        // 4/4 in quarter-note beats to 3840, then 3/8 in eighths, through the last note; each a sixteenth long.
        Assert.AreSequenceEqual(
            [
                (0L, 12), (480L, 13), (960L, 13), (1440L, 13), (1920L, 12), (2400L, 13), (2880L, 13), (3360L, 13),
                (3840L, 12), (4080L, 13), (4320L, 13), (4560L, 12), (4800L, 13),
            ],
            events.Where(e => e.Event.StartsWith("on ", StringComparison.Ordinal)).Select(e => (e.Tick, int.Parse(e.Event.Split(' ')[1], CultureInfo.InvariantCulture))).ToList());
        Assert.IsTrue(events.Where(e => e.Event.StartsWith("off ", StringComparison.Ordinal)).All(e => e.Tick % 240 == 120));
        Assert.AreEqual((0L, "name BEAT"), events[0]);
    }

    [TestMethod]
    public void BeatReachesThePassthroughTracks()
    {
        var chart = MidiChartLoader.Load(MidiFixtures.Named(nameof(MidiFixtures.Passthrough)).Midi, ChartReadOptions.Default).Chart;
        chart.AddPassthrough(new PassthroughTrack([.. "MTrk"u8, 0, 0, 0, 5, 0x8F, 0, 0xFF, 0x2F, 0], null));

        var events = EventsOf(MidiChartWriter.Write(chart), "BEAT");

        // The track added ends at tick 1920.
        Assert.AreEqual(1920, events.Where(e => e.Event.StartsWith("on ", StringComparison.Ordinal)).Max(e => e.Tick));
    }

    /// <summary>Writes <paramref name="chart"/> checked with the settings it's saved with, and checks it read back as written.</summary>
    private static ChartWriteResult AssertWritesChecked(EditableChart chart)
    {
        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));
        Assert.IsTrue(
            result.IsValid,
            $"{DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
        return result;
    }
}
