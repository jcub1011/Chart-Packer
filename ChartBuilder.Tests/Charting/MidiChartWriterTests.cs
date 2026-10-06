using System.Globalization;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using static ChartBuilder.Tests.Charting.MidiChartLoaderTests;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class MidiChartWriterTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    // Every fixture.

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void EveryFixtureReadsBackAsItsNormalizedModel(string name)
    {
        // AC C17.4.
        var (chart, after) = Loaded(name);

        var reload = MidiChartLoader.Load(MidiChartWriter.Write(chart), after);

        var differences = ChartModelComparer.Differences(MidiChartWriter.Normalize(chart), reload.Chart);
        Assert.IsEmpty(differences, DescribeModel(differences));
        Assert.IsEmpty(reload.Issues, string.Join("; ", reload.Issues));
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void SavingWithoutEditsKeepsPassthroughRawEventsAndWhatTheGamesRead(string name)
    {
        // AC C2.1.
        var fixture = MidiFixtures.Named(name);
        var (chart, after) = Loaded(name);

        byte[] written = MidiChartWriter.Write(chart);

        // Every passthrough chunk is a chunk of the written file, byte for byte, in its order.
        var chunks = MidiFile.ReadChunks(written).Chunks.Select(c => written[c.Start..c.End]).ToList();
        var passthrough = chunks.Where(c => chart.Passthrough.Any(p => p.Chunk.Span.SequenceEqual(c))).ToList();
        Assert.AreSequenceEqual(
            chart.Passthrough.Select(p => Convert.ToHexString(p.Chunk.Span)).ToList(),
            passthrough.Select(Convert.ToHexString).ToList());

        var instruments = chart.Parts.Select(p => p.Instrument).ToList();
        var differences = ChartReadBack.Compare(MidiChartReader.Read(fixture.Midi, fixture.Options), MidiChartReader.Read(written, after), instruments);
        Assert.IsEmpty(differences, GameViewTests.Describe(differences));

        // Every raw event is back at its tick.
        var reload = MidiChartLoader.Load(written, after).Chart;
        Assert.AreSequenceEqual(chart.TempoTrackRaw.ToList(), reload.TempoTrackRaw.ToList());
        Assert.AreSequenceEqual(chart.EventsRaw.ToList(), reload.EventsRaw.ToList());
        foreach (var part in chart.Parts)
            Assert.AreSequenceEqual(part.RawEvents.ToList(), reload.PartOf(part.Instrument)!.RawEvents.ToList(), $"{part.Instrument}");
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void AtEveryHopoThresholdTheWrittenFileReadsAsTheGameView(string name)
    {
        // AC C17.2 and AC C5.2.
        var (chart, after) = Loaded(name);
        var saved = MidiChartWriter.Normalize(chart);
        byte[] written = MidiChartWriter.Write(chart);

        var thresholds = after.MidiHopoThresholds(chart.Resolution);
        foreach (long threshold in thresholds)
        {
            var options = after with { MidiHopoTicks = threshold };

            var read = ChartReader.Read(written, "notes.mid", options);

            var differences = ChartReadBack.Compare(GameView.Project(saved, options), read, GameView.Instruments);
            Assert.IsEmpty(differences, $"At a HOPO threshold of {threshold}: {GameViewTests.Describe(differences)}");
        }

        Assert.HasCount(2, thresholds);
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void WriteCheckedIsValidForEveryFixture(string name)
    {
        var (chart, after) = Loaded(name);

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, $"{DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
        Assert.AreSequenceEqual(MidiChartWriter.Write(chart), result.Bytes);
        Assert.IsEmpty(ChartModelComparer.Differences(MidiChartWriter.Normalize(chart), result.Saved));
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void WritingIsDeterministicAndIdempotent(string name)
    {
        var (chart, after) = Loaded(name);

        byte[] first = MidiChartWriter.Write(chart);
        byte[] again = MidiChartWriter.Write(chart.Clone());
        byte[] rewritten = MidiChartWriter.Write(MidiChartLoader.Load(first, after).Chart);

        Assert.AreSequenceEqual(first, again);
        Assert.AreSequenceEqual(first, rewritten);
    }

    // Duplicate tracks.

    [TestMethod]
    public void TheEditedTrackIsTheOneTheReaderChoosesOverADuplicate()
    {
        var (chart, after) = Loaded(nameof(MidiFixtures.DuplicateTrack));

        byte[] written = MidiChartWriter.Write(chart);

        // The second PART BASS passes through after the parts, so the reader keeps the first: the one written.
        AssertEditedTracksChosen(written, chart);
        Assert.IsTrue(MidiChartWriter.WriteChecked(chart, after).IsValid);
    }

    [TestMethod]
    public void ADuplicateThatWouldReplaceAPartIsWrittenBeforeIt()
    {
        // The first PART GUITAR passes through, replaced by the second; the second PART BASS passes through.
        byte[] midi = Song()
            .Track("PART GUITAR").Note(0, 97)
            .Track("PART BASS").Note(0, 96)
            .Track("PART BASS").Note(0, 99)
            .Track("PART GUITAR").Note(0, 96)
            .Build();
        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        var result = MidiChartWriter.WriteChecked(chart, ChartReadOptions.Default);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        AssertEditedTracksChosen(result.Bytes, chart);
        Assert.AreSequenceEqual(
            ["Song", "PART GUITAR", "PART GUITAR", "PART BASS", "EVENTS", "BEAT", "PART BASS"],
            TrackNames(result.Bytes));
        Assert.AreSequenceEqual(chart.Passthrough.ToList(), result.Saved.Passthrough.ToList());
    }

    [TestMethod]
    public void ADuplicateThatMustComeAfterThePartsIsMovedPastOneThatMustComeBefore()
    {
        // Passed through: the second PART BASS, which must come after the parts, then the first PART GUITAR, which
        // must come before them. They can't keep their order.
        byte[] midi = Song()
            .Track("PART BASS").Note(0, 96)
            .Track("PART BASS").Note(0, 99)
            .Track("PART GUITAR").Note(0, 97)
            .Track("PART GUITAR").Note(0, 96)
            .Build();
        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        var result = MidiChartWriter.WriteChecked(chart, ChartReadOptions.Default);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        AssertEditedTracksChosen(result.Bytes, chart);
        Assert.AreSequenceEqual([chart.Passthrough[1], chart.Passthrough[0]], result.Saved.Passthrough.ToList());
        Assert.AreSequenceEqual(
            ["Song", "PART GUITAR", "PART GUITAR", "PART BASS", "EVENTS", "BEAT", "PART BASS"],
            TrackNames(result.Bytes));
    }

    // Tempo map.

    [TestMethod]
    public void TemposTimeSignaturesAndAnchorsRoundTrip()
    {
        // AC C4.4.
        var (loaded, after) = Loaded(nameof(MidiFixtures.TempoMap));
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 400_000));
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 300_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(3840, 6, 8, 12, 4));
        chart.TempoMap.Anchors.Add(new TempoAnchor(1920, -2_500_001));
        chart.TempoMap.Anchors.Add(new TempoAnchor(0, 0));

        foreach (var (written, options) in ((EditableChart, ChartReadOptions)[])[(loaded, after), (chart, ChartReadOptions.Default)])
        {
            var reload = MidiChartLoader.Load(MidiChartWriter.Write(written), options).Chart.TempoMap;

            Assert.AreSequenceEqual(written.TempoMap.Tempos.ToList(), reload.Tempos.ToList());
            Assert.AreSequenceEqual(written.TempoMap.TimeSignatures.ToList(), reload.TimeSignatures.ToList());
            Assert.AreSequenceEqual(written.TempoMap.Anchors.ToList(), reload.Anchors.ToList());
        }

        // Two tempos at a tick keep their order: the games play the last.
        Assert.AreSequenceEqual(
            [new TempoChange(1920, 400_000), new TempoChange(1920, 300_000)],
            MidiChartLoader.Load(MidiChartWriter.Write(chart), ChartReadOptions.Default).Chart.TempoMap.Tempos.At(1920).ToArray());
        Assert.AreEqual(3, loaded.TempoMap.Tempos.Count);
        Assert.HasCount(1, loaded.TempoMap.Anchors);
    }

    // Notes.

    [TestMethod]
    public void ForcedTappedOpenAndDisjointNotesReadBackWithTheirTypes()
    {
        // AC C5.1.
        var chart = NewChart(ChartInstrument.Guitar);
        var expert = chart.PartOf(ChartInstrument.Guitar)![Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0));
        expert.Notes.Add(new EditableNote(120, 1, 1)); // A natural HOPO, forced strum.
        expert.SetFlags(120, ChordFlags.ForceStrum);
        expert.Notes.Add(new EditableNote(960, 1, 2)); // A strum, forced HOPO.
        expert.SetFlags(960, ChordFlags.ForceHopo);
        expert.Notes.Add(new EditableNote(1920, 1, 3));
        expert.SetFlags(1920, ChordFlags.Tap);
        expert.Notes.Add(new EditableNote(2880, 1, ChartNote.OpenLane));
        expert.Notes.Add(new EditableNote(3840, 480, 0)); // A disjoint chord.
        expert.Notes.Add(new EditableNote(3840, 240, 1));

        var read = ChartReader.Read(MidiChartWriter.Write(chart), "notes.mid");

        Assert.AreSequenceEqual(
            [
                new ChartNote(0, 0, 0, NoteFlags.None),
                new ChartNote(120, 0, 1, NoteFlags.None),
                new ChartNote(960, 0, 2, NoteFlags.Hopo),
                new ChartNote(1920, 0, 3, NoteFlags.Tap),
                new ChartNote(2880, 0, ChartNote.OpenLane, NoteFlags.None),
                new ChartNote(3840, 480, 0, NoteFlags.None),
                new ChartNote(3840, 240, 1, NoteFlags.None),
            ],
            read.Tracks[new ChartTrackKey(ChartInstrument.Guitar, Expert)].Notes.ToList());
        Assert.IsTrue(MidiChartWriter.WriteChecked(chart, ChartReadOptions.Default).IsValid);
    }

    [TestMethod]
    public void AddingAGhostNoteWritesOnlyThatNoteWithAnotherVelocity()
    {
        // AC C6.4.
        byte[] midi = Song()
            .Track("PART DRUMS")
            .Note(0, 97, velocity: 127).Note(480, 98, velocity: 127).Note(960, 96, velocity: 127)
            .Build();
        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        Assert.IsFalse(drums.ChartDynamics);

        drums[Expert].Notes.Add(new EditableNote(1440, 1, 3, NoteDynamics.Ghost));
        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        var events = EventsOf(result.Bytes, "PART DRUMS");
        Assert.AreSequenceEqual(
            [(0L, "on 97 100"), (480L, "on 98 100"), (960L, "on 96 100"), (1440L, "on 99 1")],
            events.Where(e => e.Event.StartsWith("on ", StringComparison.Ordinal)).ToList());
        Assert.Contains((0L, "text [ENABLE_CHART_DYNAMICS]"), events);
        var notes = result.Saved.PartOf(ChartInstrument.Drums)![Expert].Notes;
        Assert.AreSequenceEqual([NoteDynamics.None, NoteDynamics.None, NoteDynamics.None, NoteDynamics.Ghost], notes.Select(n => n.Dynamics).ToList());
    }

    // The check.

    [TestMethod]
    [DoNotParallelize]
    public void WriteCheckedReportsAndLogsWhatDoesNotReadBack()
    {
        // A drums big rock ending with no [coda] before it reads back as a drum fill; an anchor without a tempo
        // marker is left out when it's read back.
        var chart = NewChart(ChartInstrument.Drums);
        chart.Name = "Unsaveable";
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[Expert].Notes.Add(new EditableNote(480, 1, 1));
        drums.Phrases.Add(new Phrase(PhraseKind.BigRockEnding, 480, 480));
        chart.TempoMap.Anchors.Add(new TempoAnchor(960, 1_000_000));
        var sink = new Sink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        ChartWriteResult result;
        try
        {
            result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));
        }
        finally
        {
            Log.Logger = previous;
        }

        Assert.IsFalse(result.IsValid);
        Assert.AreSequenceEqual(
            [(ModelDifferenceKind.Anchor, (long?)960), (ModelDifferenceKind.Phrase, 480)],
            result.ModelDifferences.Select(d => (d.Kind, d.Tick)).ToList());
        Assert.IsEmpty(result.GameDifferences);
        var issue = Assert.ContainsSingle(result.ReloadIssues);
        Assert.AreEqual(ChartLoadIssueKind.StrayAnchor, issue.Kind);

        var error = Assert.ContainsSingle(sink.Events.Where(e => e.Level == LogEventLevel.Error && IsFrom(e, typeof(MidiChartWriter))));
        Assert.AreEqual("\"Unsaveable\"", error.Properties["Name"].ToString());
        Assert.AreEqual("2", error.Properties["ModelDifferences"].ToString());
        Assert.AreEqual("1", error.Properties["ReloadIssues"].ToString());
        Assert.Contains("Phrase Drums  at 480", error.Properties["First"].ToString());
    }

    [TestMethod]
    public void WriteCheckedReportsWhatTheGamesReadDifferently()
    {
        // Read with star power on 103, the solo becomes star power and the star power is lost.
        var chart = NewChart(ChartInstrument.Guitar);
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar[Expert].Notes.Add(new EditableNote(0, 1, 0));
        guitar[Expert].Notes.Add(new EditableNote(960, 1, 0));
        guitar.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 480));
        guitar.Phrases.Add(new Phrase(PhraseKind.Solo, 960, 480));

        var result = MidiChartWriter.WriteChecked(chart, new ChartReadOptions(MultiplierNote: 103));

        Assert.IsFalse(result.IsValid);
        Assert.AreSequenceEqual(
            [ReadBackDifferenceKind.NoteFlags, ReadBackDifferenceKind.StarPower],
            result.GameDifferences.Select(d => d.Kind).ToList());
        Assert.IsTrue(MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, new ChartReadOptions(MultiplierNote: 103))).IsValid);
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FiveLane)]
    public void ANewChartWritesCheckedAndReadsBackAsItsSettings(DrumsVariant variant)
    {
        var settings = new NewChartSettings("New Song", 960, 97.5, 7, 8, NewChartSettings.Instruments.ToHashSet(), variant);
        var chart = EditableChart.CreateNew(settings);

        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default));

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        Assert.AreSequenceEqual(result.Bytes, NewChartMidi.Build(settings));
        Assert.AreSequenceEqual(
            ["New Song", .. NewChartSettings.Instruments.Order().Select(i => ChartToMidi.TrackNames[i]), "EVENTS", "BEAT"],
            TrackNames(result.Bytes));
        Assert.AreEqual(variant, MidiChartLoader.Load(result.Bytes, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default)).Chart.DrumsVariant);
    }

    [TestMethod]
    public void TheSettingsAfterASaveClearTheMultiplierNoteAndSayTheDrumVariant()
    {
        var song = new ChartReadOptions(ProDrums: true, SustainCutoff: 60, MultiplierNote: 103);
        var chart = NewChart(ChartInstrument.Drums);

        Assert.AreEqual(song with { MultiplierNote = null, ProDrums = false, FiveLaneDrums = false }, MidiChartWriter.SettingsAfterSave(chart, song));
        chart.DrumsVariant = DrumsVariant.Pro;
        Assert.AreEqual(song with { MultiplierNote = null, FiveLaneDrums = false }, MidiChartWriter.SettingsAfterSave(chart, song));
        chart.DrumsVariant = DrumsVariant.FiveLane;
        Assert.AreEqual(song with { MultiplierNote = null, FiveLaneDrums = true }, MidiChartWriter.SettingsAfterSave(chart, song));
        Assert.AreEqual(song with { MultiplierNote = null }, MidiChartWriter.SettingsAfterSave(NewChart(ChartInstrument.Guitar), song));
    }

    // Helpers.

    /// <summary>A fixture loaded, and the settings it's read with once saved.</summary>
    private static (EditableChart Chart, ChartReadOptions After) Loaded(string name)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;
        return (chart, MidiChartWriter.SettingsAfterSave(chart, fixture.Options));
    }

    /// <summary>A new 480-tick chart at 120 BPM in 4/4 with an empty part for <paramref name="instrument"/>, 4-lane if drums.</summary>
    internal static EditableChart NewChart(ChartInstrument instrument) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { instrument }, DrumsVariant.FourLane));

    /// <summary>Each track's name, in order.</summary>
    internal static List<string?> TrackNames(byte[] midi) => [.. MidiFile.ReadChunks(midi).Chunks.Skip(1).Select(c => MidiFile.NameOf(midi, c))];

    /// <summary>The events of the track named <paramref name="name"/>, each as its tick and a few words.</summary>
    internal static List<(long Tick, string Event)> EventsOf(byte[] midi, string name)
    {
        var chunk = MidiFile.ReadChunks(midi).Chunks.Skip(1).First(c => MidiFile.NameOf(midi, c) == name);
        return [.. MidiFile.ReadEvents(midi, chunk).Select(e => (e.Tick, Describe(midi, e)))];
    }

    private static string Describe(byte[] midi, MidiEvent e)
    {
        if (e.IsMeta)
        {
            var (type, start, length) = MidiFile.MetaOf(midi, e);
            string text = MidiFile.DecodeText(midi.AsSpan(start, length));
            return type switch
            {
                0x03 => $"name {text}",
                MidiFile.EndOfTrack => "end",
                >= 0x01 and <= 0x09 => $"text {text}",
                _ => string.Create(CultureInfo.InvariantCulture, $"meta {type:X2}"),
            };
        }

        if (MidiTrackNotes.PhaseShiftOf(midi, e) is var (difficulty, code, on))
            return string.Create(CultureInfo.InvariantCulture, $"sysex {difficulty} {code} {(on ? "on" : "off")}");
        return e.Kind switch
        {
            0x90 => string.Create(CultureInfo.InvariantCulture, $"on {midi[e.DataStart]} {midi[e.DataStart + 1]}"),
            0x80 => string.Create(CultureInfo.InvariantCulture, $"off {midi[e.DataStart]}"),
            _ => $"raw {e.Status:X2}{Convert.ToHexString(midi, e.DataStart, e.DataLength)}",
        };
    }

    /// <summary>Checks that the reader takes each of the chart's parts from a track written for it, not a passthrough one.</summary>
    private static void AssertEditedTracksChosen(byte[] written, EditableChart chart)
    {
        var (_, chunks) = MidiChartReader.ChunksOf(written);
        var parts = MidiChartReader.PartTracksOf(written, chunks);
        foreach (var part in chart.Parts)
        {
            var chunk = chunks[parts[part.Instrument].Chunk];
            byte[] chosen = written[chunk.Start..chunk.End];
            Assert.IsFalse(chart.Passthrough.Any(p => p.Chunk.Span.SequenceEqual(chosen)), $"{part.Instrument} was read from a passthrough track.");
            Assert.AreEqual(ChartToMidi.TrackNames[part.Instrument], parts[part.Instrument].Name);
        }
    }

    internal static string DescribeModel(IEnumerable<ModelDifference> differences) => string.Join("; ", differences.Select(d => d.Description));

    private static bool IsFrom(LogEvent e, Type type) =>
        e.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context) && context.ToString() == $"\"{type.FullName}\"";

    private sealed class Sink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}
