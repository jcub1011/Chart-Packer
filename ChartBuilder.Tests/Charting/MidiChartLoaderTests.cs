using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Tests.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class MidiChartLoaderTests
{
    // A .chart with every kind of part, and an Offset.
    private const string SampleChart = """
        [Song]
        {
          Name = "Test"
          Offset = 0.25
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = TS 4
          0 = B 120000
          768 = B 60000
          768 = TS 6 3
        }
        [Events]
        {
          0 = E "section Intro"
        }
        [ExpertSingle]
        {
          0 = N 0 96
          0 = N 1 96
          192 = N 2 0
          240 = N 3 0
          240 = N 5 0
          480 = N 7 0
          600 = N 4 0
          600 = N 6 0
          0 = S 2 300
        }
        [HardDoubleBass]
        {
          0 = N 0 0
        }
        [ExpertDrums]
        {
          0 = N 0 0
          48 = N 32 0
          0 = N 2 0
          0 = N 66 0
          96 = N 3 0
          192 = N 4 0
          192 = N 68 0
        }
        [ExpertGHLGuitar]
        {
          0 = N 0 0
          0 = N 3 0
          96 = N 8 0
          192 = N 7 0
        }
        """;

    // Hard marks its blue as a cymbal, so its yellow at 384 is meant as a tom: the conversion warns.
    private const string WarnedChart = """
        [Song]
        {
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = B 120000
        }
        [ExpertDrums]
        {
          0 = N 3 0
          0 = N 67 0
          384 = N 2 0
          384 = N 66 0
        }
        [HardDrums]
        {
          0 = N 3 0
          0 = N 67 0
          384 = N 2 0
        }
        """;

    /// <summary>A song's tempo track: named <c>Song</c>, at 120 BPM in 4/4 from tick 0.</summary>
    internal static MidiBuilder Song(int resolution = 480) => new MidiBuilder(resolution).Track("Song").Tempo(0, 120).TimeSignature(0, 4, 2);

    internal static ChartLoadResult Load(MidiBuilder midi, ChartReadOptions? options = null) =>
        MidiChartLoader.Load(midi.Build(), options ?? ChartReadOptions.Default);

    /// <summary>A text meta event's bytes, status first, as a <see cref="RawEvent"/> holds them.</summary>
    internal static byte[] TextBytes(byte type, string text) => [0xFF, type, (byte)text.Length, .. Encoding.ASCII.GetBytes(text)];

    /// <summary>
    /// Checks that every note of the model is a note <paramref name="read"/> has, at the same tick and lane, and the
    /// other way round: frets by lane, open notes included; drums by the lane the variant plays each pad in, the kick
    /// and the 2× kick apart. Lengths and flags are the game view's, which comes later.
    /// </summary>
    internal static void AssertNotesMatchReader(EditableChart chart, ChartDocument read)
    {
        foreach (var instrument in read.Instruments.Where(i => i != ChartInstrument.Keys))
            Assert.IsNotNull(chart.PartOf(instrument), $"The model has no {instrument} part.");

        foreach (var part in chart.Parts)
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                var model = part[difficulty].Notes
                    .Select(n => (n.Tick, Lane: LaneOf(part.Kind, n, chart.DrumsVariant), n.DoubleKick))
                    .Order()
                    .ToList();
                var reader = (read.Tracks.TryGetValue(new ChartTrackKey(part.Instrument, difficulty), out var track) ? track.Notes : [])
                    .Select(n => (n.Tick, n.Lane, DoubleKick: n.Flags.HasFlag(NoteFlags.DoubleKick)))
                    .Order()
                    .ToList();
                Assert.AreSequenceEqual(reader, model, $"{part.Instrument} {difficulty}");
            }
        }

        static int LaneOf(ChartInstrumentKind kind, EditableNote note, DrumsVariant? variant) =>
            kind != ChartInstrumentKind.Drums ? note.Lane
            : note.Lane == 0 ? ChartNote.OpenLane
            : variant == DrumsVariant.FiveLane ? note.Lane - 1
            : Math.Min(note.Lane, 4) - 1;
    }

    // Tempo track.

    [TestMethod]
    public void TheTempoTrackGivesTheNameTemposAndTimeSignatures()
    {
        var chart = Load(Song()
            .Tempo(1920, 90)
            .Meta(960, 0x58, [6, 3, 12, 8])
            .Meta(1920, 0x58, [7, 3])).Chart;

        Assert.AreEqual("Song", chart.Name);
        Assert.AreEqual(480, chart.Resolution);
        Assert.AreSequenceEqual([new TempoChange(0, 500_000), new TempoChange(1920, 666_667)], chart.TempoMap.Tempos.ToList());
        // The metronome bytes are kept as written, and given MIDI's usual ones when the event leaves them out.
        Assert.AreSequenceEqual(
            [new TimeSignatureMarker(0, 4, 4, 24, 8), new TimeSignatureMarker(960, 6, 8, 12, 8), new TimeSignatureMarker(1920, 7, 8, 24, 8)],
            chart.TempoMap.TimeSignatures.ToList());
        Assert.IsEmpty(chart.TempoTrackRaw);
        Assert.IsEmpty(chart.TempoMap.Anchors);
    }

    [TestMethod]
    public void TempoMarkersAreAsLateAsTheFirstEventSaysAndOtherEventsWhereTheyAre()
    {
        // The tempo track's first event is 10 ticks in, so YARG reads every tempo 10 ticks later than written.
        byte[] midi = new MidiBuilder().Track().Text(10, "hello").Tempo(10, 120).Tempo(480, 60).Build();

        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        Assert.IsNull(chart.Name);
        Assert.AreSequenceEqual([new TempoChange(20, 500_000), new TempoChange(490, 1_000_000)], chart.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new RawEvent(10, TextBytes(0x01, "hello"))], chart.TempoTrackRaw.ToList());
        Assert.IsTrue(chart.TempoMap.ToTempoMap().SameAs(MidiChartReader.Read(midi, ChartReadOptions.Default).Tempo));
    }

    [TestMethod]
    public void AnAnchorIsReadAtItsTempoMarkerAndLeftOutElsewhere()
    {
        var result = Load(Song()
            .Tempo(960, 100)
            .Text(0, "[chartpacker_anchor 007]")
            .Text(0, "[chartpacker_anchor soon]")
            .Text(480, "[chartpacker_anchor 250000]")
            .Text(960, "[chartpacker_anchor 1500000]")
            .Text(960, "[chartpacker_anchor 1500001]"));
        var chart = result.Chart;

        Assert.AreSequenceEqual([new TempoAnchor(960, 1_500_000)], chart.TempoMap.Anchors.ToList());
        // Not written as the writer writes one: kept as it is, and not an issue, as it isn't an anchor. The anchors
        // without a marker of their own are left out.
        Assert.AreSequenceEqual(
            [
                new RawEvent(0, TextBytes(0x01, "[chartpacker_anchor 007]")),
                new RawEvent(0, TextBytes(0x01, "[chartpacker_anchor soon]")),
            ],
            chart.TempoTrackRaw.ToList());
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.StrayAnchor, null, null, 480),
                new ChartLoadIssue(ChartLoadIssueKind.StrayAnchor, null, null, 960),
            ],
            result.Issues.ToList());
    }

    [TestMethod]
    public void ATempoOrTimeSignatureTheModelCannotHoldIsLeftOutWithAnIssue()
    {
        var result = Load(Song().Meta(480, 0x51, [0, 0, 0]).Meta(960, 0x58, [0, 2, 24, 8]));
        var chart = result.Chart;

        Assert.AreSequenceEqual([new TempoChange(0, 500_000)], chart.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([new TimeSignatureMarker(0, 4, 4)], chart.TempoMap.TimeSignatures.ToList());
        Assert.IsEmpty(chart.TempoTrackRaw);
        Assert.AreSequenceEqual(
            [
                new ChartLoadIssue(ChartLoadIssueKind.UnholdableTempo, null, null, 480),
                new ChartLoadIssue(ChartLoadIssueKind.UnholdableTimeSignature, null, null, 960),
            ],
            result.Issues.ToList());
    }

    [TestMethod]
    public void ATempoTrackTheModelCannotHoldAllOfStillSaves()
    {
        // Kept raw, each would be an issue again when the save reads it back, and the chart could never be saved.
        var chart = Load(Song()
            .Meta(480, 0x51, [0, 0, 0])
            .Meta(960, 0x58, [0, 2, 24, 8])
            .Text(1440, "[chartpacker_anchor 250000]")
            .Track("PART GUITAR").Note(0, 96)).Chart;

        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));

        Assert.IsTrue(result.IsValid, $"{MidiChartWriterTests.DescribeModel(result.ModelDifferences)} {string.Join("; ", result.ReloadIssues)}");
    }

    [TestMethod]
    public void TheTempoTracksOtherEventsAreKeptRaw()
    {
        byte[] midi = Song()
            .Meta(0, 0x59, [0xFE, 0])
            .Meta(480, 0x06, "Verse"u8.ToArray())
            .Text(960, "free")
            .Meta(1200, 0x58, [3, 7, 24, 8])
            .Meta(1500, 0x51, [1, 2])
            .Build();

        var result = MidiChartLoader.Load(midi, ChartReadOptions.Default);

        // A time signature of 2^7 and a tempo of two bytes aren't read by YARG either; the end of the track is the writer's.
        Assert.AreSequenceEqual(
            [
                new RawEvent(0, [0xFF, 0x59, 2, 0xFE, 0]),
                new RawEvent(480, TextBytes(0x06, "Verse")),
                new RawEvent(960, TextBytes(0x01, "free")),
                new RawEvent(1200, [0xFF, 0x58, 4, 3, 7, 24, 8]),
                new RawEvent(1500, [0xFF, 0x51, 2, 1, 2]),
            ],
            result.Chart.TempoTrackRaw.ToList());
        Assert.IsEmpty(result.Issues);
        Assert.IsTrue(result.Chart.TempoMap.ToTempoMap().SameAs(MidiChartReader.Read(midi, ChartReadOptions.Default).Tempo));
    }

    // EVENTS.

    [TestMethod]
    public void EventsKeepsItsTextEventsWithTheirTypesAndOtherEventsRaw()
    {
        var chart = Load(Song()
            .Track("EVENTS")
            .Text(0, "[music_start]")
            .Text(480, "[section Intro]")
            .Meta(960, 0x06, "[prc_verse]"u8.ToArray())
            .Note(1000, 50, 20)
            .Text(1920, "free text")
            .Text(3840, "[end]")).Chart;

        Assert.AreSequenceEqual(
            [
                new TextEvent(0, 0x01, "[music_start]"),
                new TextEvent(480, 0x01, "[section Intro]"),
                new TextEvent(960, 0x06, "[prc_verse]"),
                new TextEvent(1920, 0x01, "free text"),
                new TextEvent(3840, 0x01, "[end]"),
            ],
            chart.Events.ToList());
        Assert.AreSequenceEqual([new RawEvent(1000, [0x90, 50, 100]), new RawEvent(1020, [0x90, 50, 0])], chart.EventsRaw.ToList());
        Assert.IsEmpty(chart.Passthrough);
    }

    // Tracks.

    [TestMethod]
    public void ALegacyTrackIsEditedAsItsInstrument()
    {
        var chart = Load(Song().Track("T1 GEMS").Note(0, 96).Track("PART REAL_DRUMS_PS").Note(0, 97)).Chart;

        Assert.AreSequenceEqual([ChartInstrument.Guitar, ChartInstrument.Drums], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.AreSequenceEqual([new EditableNote(0, 60, 0)], chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.ToList());
        Assert.AreSequenceEqual([new EditableNote(0, 60, 1)], chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.ToList());
        Assert.IsEmpty(chart.Passthrough);
    }

    [TestMethod]
    public void OfTwoTracksForAPartTheOneTheReaderKeepsIsEditedAndTheOtherPassesThrough()
    {
        // PART GUITAR and PART DRUMS replace an earlier track of theirs; any other is skipped after the first.
        byte[] midi = Song()
            .Track("T1 GEMS").Note(0, 97)
            .Track("PART GUITAR").Note(0, 98)
            .Track("PART DRUMS").Note(0, 97)
            .Track("PART DRUMS").Note(0, 98)
            .Track("PART BASS").Note(0, 97)
            .Track("PART BASS").Note(0, 98)
            .Build();
        var chunks = MidiFile.ReadChunks(midi).Chunks;

        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        foreach (var instrument in (ChartInstrument[])[ChartInstrument.Guitar, ChartInstrument.Drums])
            Assert.AreEqual(2, chart.PartOf(instrument)![ChartDifficulty.Expert].Notes.Single().Lane, instrument.ToString());
        Assert.AreEqual(1, chart.PartOf(ChartInstrument.Bass)![ChartDifficulty.Expert].Notes.Single().Lane);
        Assert.AreSequenceEqual(["T1 GEMS", "PART DRUMS", "PART BASS"], chart.Passthrough.Select(t => t.Name).ToList());
        Assert.AreSequenceEqual(
            [.. new[] { 2, 4, 7 }.Select(i => Convert.ToHexString(midi[chunks[i].Start..chunks[i].End]))],
            chart.Passthrough.Select(t => Convert.ToHexString(t.Chunk.Span)).ToList());
        AssertNotesMatchReader(chart, MidiChartReader.Read(midi, ChartReadOptions.Default));
    }

    [TestMethod]
    public void TracksNotEditedPassThroughByteForByteInTheirOrderAndBeatIsNotKept()
    {
        // AC C2.1.
        byte[] midi = Song()
            .Track("PART VOCALS").Note(0, 60).Text(0, "Hel-")
            .Track("PART GUITAR").Note(0, 96)
            .Track("PART KEYS").Note(0, 97)
            .Track("BEAT").Note(0, 12).Note(480, 13)
            .Track("PART DRUMS_2X").Note(0, 96)
            .Track("VENUE").Text(0, "[lighting (verse)]")
            .Track("EVENTS").Text(0, "[music_start]")
            .Track("SOMETHING ELSE").Raw(0, 0xB0, 7, 100)
            .Build();
        midi = InsertChunk(midi, after: 5, [.. "XFIH"u8, 0, 0, 0, 2, 1, 2]);
        var chunks = MidiFile.ReadChunks(midi).Chunks;

        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        // Header, tempo, vocals, guitar, keys, beat, XFIH, drums 2x, venue, events, something else.
        Assert.AreSequenceEqual(
            ["PART VOCALS", "PART KEYS", null, "PART DRUMS_2X", "VENUE", "SOMETHING ELSE"],
            chart.Passthrough.Select(t => t.Name).ToList());
        Assert.AreSequenceEqual(
            [.. new[] { 2, 4, 6, 7, 8, 10 }.Select(i => Convert.ToHexString(midi[chunks[i].Start..chunks[i].End]))],
            chart.Passthrough.Select(t => Convert.ToHexString(t.Chunk.Span)).ToList());
        Assert.IsFalse(chart.Passthrough[2].IsTrack);
        Assert.AreSequenceEqual([ChartInstrument.Guitar], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.AreSequenceEqual([TextEvent.Of(0, "[music_start]")], chart.Events.ToList());
    }

    [TestMethod]
    public void AFileTheReaderCannotReadFailsTheSameWay()
    {
        byte[] good = Song().Track("PART GUITAR").Note(0, 96).Build();
        // A track whose first event is a data byte with no status before it.
        byte[] malformed = [.. good, .. "MTrk"u8, 0, 0, 0, 2, 0, 0x40];
        byte[] notMidi = "not a MIDI file"u8.ToArray();

        foreach (byte[] midi in (byte[][])[malformed, notMidi])
        {
            var expected = Assert.ThrowsExactly<ChartFormatException>(() => MidiChartReader.Read(midi, ChartReadOptions.Default));
            var actual = Assert.ThrowsExactly<ChartFormatException>(() => MidiChartLoader.Load(midi, ChartReadOptions.Default));
            Assert.AreEqual(expected.Message, actual.Message);
        }
    }

    // Drums variant.

    [TestMethod]
    public void TheDrumsVariantComesFromTheSongIniWhenItSaysOne()
    {
        // A fifth-pad note, which would make them five-lane if song.ini didn't say.
        var drums = Song().Track("PART DRUMS").Note(0, 101);

        Assert.AreEqual(DrumsVariant.FourLane, Load(drums, new ChartReadOptions(ProDrums: false, FiveLaneDrums: false)).Chart.DrumsVariant);
        Assert.AreEqual(DrumsVariant.Pro, Load(drums, new ChartReadOptions(ProDrums: true, FiveLaneDrums: false)).Chart.DrumsVariant);
        Assert.AreEqual(DrumsVariant.FiveLane, Load(Song().Track("PART DRUMS").Note(0, 97), new ChartReadOptions(FiveLaneDrums: true)).Chart.DrumsVariant);
    }

    [TestMethod]
    public void TheDrumsVariantIsDetectedWhenTheSongIniSaysNothing()
    {
        Assert.AreEqual(DrumsVariant.FiveLane, Load(Song().Track("PART DRUMS").Note(0, 97).Note(480, 101)).Chart.DrumsVariant);
        Assert.AreEqual(DrumsVariant.Pro, Load(Song().Track("PART DRUMS").Note(0, 98).Note(0, 110)).Chart.DrumsVariant);
        Assert.AreEqual(DrumsVariant.Pro, Load(Song().Track("PART DRUMS").Note(0, 97)).Chart.DrumsVariant);
        Assert.IsNull(Load(Song().Track("PART GUITAR").Note(0, 96)).Chart.DrumsVariant);
    }

    // .chart import.

    [TestMethod]
    public void ImportingAChartGivesTheConversionsOffsetAndWarningsAndItsNotes()
    {
        // AC C2.2.
        foreach (string text in (string[])[SampleChart, WarnedChart])
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var conversion = ChartToMidi.Convert(bytes, ChartReadOptions.Default);

            var import = MidiChartLoader.LoadChart(bytes, ChartReadOptions.Default);

            Assert.AreEqual(conversion.Offset, import.Offset);
            Assert.AreEqual(conversion.Options, import.Options);
            Assert.AreSequenceEqual(conversion.Warnings.Select(Describe).ToList(), import.Warnings.Select(Describe).ToList());
            Assert.IsEmpty(import.Load.Issues);
            AssertNotesMatchReader(import.Load.Chart, MidiChartReader.Read(conversion.Midi, conversion.Options));
        }

        var sample = MidiChartLoader.LoadChart(Encoding.UTF8.GetBytes(SampleChart), ChartReadOptions.Default);
        Assert.AreEqual(TimeSpan.FromSeconds(0.25), sample.Offset);
        Assert.AreEqual(192, sample.Load.Chart.Resolution);
        Assert.AreSequenceEqual([TextEvent.Of(0, "[section Intro]")], sample.Load.Chart.Events.ToList());
        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.StarPower, 0, 300)],
            sample.Load.Chart.PartOf(ChartInstrument.Guitar)!.Phrases.ToList());
        Assert.AreEqual(DrumsVariant.Pro, sample.Load.Chart.DrumsVariant);
        Assert.HasCount(1, MidiChartLoader.LoadChart(Encoding.UTF8.GetBytes(WarnedChart), ChartReadOptions.Default).Warnings);

        static string Describe(ChartMergeWarning w) => $"{w.Issue} {w.TrackName} {w.Difficulty} {string.Join(",", w.Times)}";
    }

    // Cross-check with the reader.

    [TestMethod]
    public void EveryNoteOfTheModelIsANoteTheReaderReads()
    {
        byte[] midi = Song()
            .Track("PART GUITAR")
            .Text(0, "[ENHANCED_OPENS]")
            .Note(0, 95).Note(480, 96).Note(480, 98).Note(960, 97)
            .Raw(1440, 0x90, 99, 100).Raw(1440, 0x91, 99, 100).Raw(1450, 0x81, 99, 0).Raw(1500, 0x80, 99, 0)
            .PhaseShift(1920, 3, MidiLayout.SysexOpen, true).Note(1920, 97).PhaseShift(1930, 3, MidiLayout.SysexOpen, false)
            .Note(0, 59).Note(480, 61).Note(960, 63)
            .Track("PART BASS")
            .Note(0, 95).Note(480, 96).Note(480, 100).Note(960, 72).Note(960, 84)
            .Track("PART GUITAR GHL")
            .Note(0, 94).Note(480, 95).Note(480, 100).Note(960, 82).Note(960, 70).Note(960, 58)
            .Track("PART DRUMS")
            .Note(0, 96).Note(0, 95).Note(0, 98).Note(480, 99).Note(480, 99, 10).Note(960, 100).Note(960, 110, 1)
            .Note(0, 71, 10).Note(0, 72, 20).Note(480, 73).Note(0, 60).Note(0, 84).Note(480, 86)
            .Build();

        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        AssertNotesMatchReader(chart, MidiChartReader.Read(midi, ChartReadOptions.Default));
        Assert.AreEqual(4, chart.Parts.Count);
        Assert.HasCount(6, chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes);
    }

    /// <summary><paramref name="midi"/> with <paramref name="chunk"/> put after its chunk <paramref name="after"/>, the header being 0.</summary>
    private static byte[] InsertChunk(byte[] midi, int after, byte[] chunk)
    {
        int at = MidiFile.ReadChunks(midi).Chunks[after].End;
        return [.. midi[..at], .. chunk, .. midi[at..]];
    }
}
