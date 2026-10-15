using System.Text;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

/// <summary>Accents and ghosts: MIDI drum velocities, read only on a drums track with <c>[ENABLE_CHART_DYNAMICS]</c>, as YARG reads them.</summary>
[TestClass]
public sealed class DrumDynamicsTests
{
    private const NoteFlags Dynamics = NoteFlags.Accent | NoteFlags.Ghost;

    [TestMethod]
    public void ChartDynamicsMakeVelocity127AnAccentAnd1AGhostOnThePads()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Text(0, "[ENABLE_CHART_DYNAMICS]")
            .Note(0, 97, velocity: 127)
            .Note(100, 98, velocity: 1)
            .Note(200, 99, velocity: 100)
            .Note(300, 100, velocity: 126)
            .Note(400, 97, velocity: 2)
            .Build();

        var notes = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums);

        // Pro drums by default: yellow, blue and green are cymbals, and an accent or ghost is marked on top.
        Assert.AreSequenceEqual(
            [NoteFlags.Accent, NoteFlags.Cymbal | NoteFlags.Ghost, NoteFlags.Cymbal, NoteFlags.Cymbal, NoteFlags.None],
            [.. notes.Select(n => n.Flags)]);
    }

    [TestMethod]
    public void WithoutChartDynamicsEveryVelocityIsANormalNote()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Note(0, 97, velocity: 127)
            .Note(100, 98, velocity: 1)
            .Build();

        var notes = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums);

        Assert.AreSequenceEqual([NoteFlags.None, NoteFlags.Cymbal], [.. notes.Select(n => n.Flags)]);
    }

    [TestMethod]
    public void KickAndDoubleKickAreNeverAccentsOrGhosts()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Text(0, "[ENABLE_CHART_DYNAMICS]")
            .Note(0, 96, velocity: 127)
            .Note(100, 96, velocity: 1)
            .Note(200, 95, velocity: 127)
            .Note(300, 95, velocity: 1)
            .Build();

        var notes = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums);

        Assert.AreSequenceEqual(
            [NoteFlags.None, NoteFlags.None, NoteFlags.DoubleKick, NoteFlags.DoubleKick],
            [.. notes.Select(n => n.Flags)]);
    }

    [TestMethod]
    public void ChartDynamicsTakeEffectForNotesLetGoAfterTheEvent()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Note(0, 97, 10, velocity: 127)  // let go before the event: normal
            .Note(40, 97, 20, velocity: 127) // pressed before it but let go after: an accent
            .Text(50, "[ENABLE_CHART_DYNAMICS]")
            .Note(100, 97, 10, velocity: 1)
            .Build();

        var notes = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums);

        Assert.AreSequenceEqual([NoteFlags.None, NoteFlags.Accent, NoteFlags.Ghost], [.. notes.Select(n => n.Flags)]);
    }

    [TestMethod]
    public void ChartDynamicsIsReadUnbracketedAndFromAnyTextTypeButCopyrightAndTrackName()
    {
        static NoteFlags FlagsWith(byte type, string text) => ChartReaderTests.Notes(ChartReader.Read(
            new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
                .Meta(0, type, Encoding.ASCII.GetBytes(text))
                .Note(0, 97, velocity: 127)
                .Build(),
            "notes.mid"), ChartInstrument.Drums).Single().Flags;

        Assert.AreEqual(NoteFlags.Accent, FlagsWith(0x01, "ENABLE_CHART_DYNAMICS"));
        Assert.AreEqual(NoteFlags.Accent, FlagsWith(0x01, " [ENABLE_CHART_DYNAMICS] "));
        Assert.AreEqual(NoteFlags.Accent, FlagsWith(0x05, "[ENABLE_CHART_DYNAMICS]"));
        Assert.AreEqual(NoteFlags.None, FlagsWith(0x02, "[ENABLE_CHART_DYNAMICS]"));
        Assert.AreEqual(NoteFlags.None, FlagsWith(0x01, "[enable_chart_dynamics]"));
    }

    [TestMethod]
    public void FretTrackWithChartDynamicsIgnoresVelocities()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR")
            .Text(0, "[ENABLE_CHART_DYNAMICS]")
            .Note(0, 96, velocity: 127)
            .Note(480, 97, velocity: 1)
            .Build();

        var notes = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Guitar);

        Assert.IsTrue(notes.All(n => (n.Flags & Dynamics) == 0));
    }

    [TestMethod]
    public void TrackNotesKnowWhereChartDynamicsStartsAndConsumeTheEvent()
    {
        byte[] bytes = new MidiBuilder()
            .Track("PART DRUMS")                 // 0
            .Note(0, 97, 10)                     // 1, 2
            .Text(20, "[ENABLE_CHART_DYNAMICS]") // 3
            .Note(30, 97, 10)                    // 4, 5
            .Text(50, "[ENABLE_CHART_DYNAMICS]") // 6: a second changes nothing
            .Build();
        var (_, _, chunks) = MidiFile.ReadChunks(bytes);
        var track = MidiTrackNotes.Of(bytes, MidiFile.ReadEvents(bytes, chunks[1]), enhancedOpens: false);

        Assert.AreEqual(3, track.ChartDynamics);
        Assert.AreSequenceEqual([false, true], [.. track.Notes.Select(track.HasChartDynamics)]);
        Assert.Contains(3, track.Consumed);
        Assert.Contains(6, track.Consumed);
    }

    [TestMethod]
    public void NoteCheckIgnoresAccentsAndGhostsOnlyWhenToldTo()
    {
        ChartNote[] expected = [new(0, 0, 0, NoteFlags.None), new(100, 0, 1, NoteFlags.Cymbal)];
        ChartNote[] actual = [new(0, 0, 0, NoteFlags.Accent), new(100, 0, 1, NoteFlags.Cymbal | NoteFlags.Ghost)];

        var masked = NoteCheck.Compare(expected, actual, NoteFlags.Accent | NoteFlags.Ghost);
        var exact = NoteCheck.Compare(expected, actual, NoteFlags.None);

        Assert.IsNull(masked.Issue);
        Assert.IsEmpty(masked.Ticks);
        Assert.AreEqual(ChartMergeIssue.MarkingsChanged, exact.Issue);
        Assert.AreSequenceEqual([0L, 100L], exact.Ticks);
    }

    [TestMethod]
    public void ChartWithAccentsAndGhostsConvertsWithoutWarningsAndReadsBackWithThem()
    {
        byte[] chart = Encoding.UTF8.GetBytes("""
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
              0 = N 1 0
              0 = N 34 0
              96 = N 2 0
              96 = N 41 0
              192 = N 0 0
              192 = N 3 0
            }
            """);

        var conversion = ChartToMidi.Convert(chart);
        var notes = ChartReaderTests.Notes(ChartReader.Read(conversion.Midi, "notes.mid", new ChartReadOptions(conversion.ProDrums)), ChartInstrument.Drums);

        Assert.IsEmpty(conversion.Warnings);
        Assert.AreSequenceEqual(
            [NoteFlags.Accent, NoteFlags.Ghost, NoteFlags.None, NoteFlags.None],
            [.. notes.Select(n => n.Flags & ~NoteFlags.Cymbal)]);
    }
}
