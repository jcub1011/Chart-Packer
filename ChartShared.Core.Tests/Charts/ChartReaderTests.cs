using System.Text;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class ChartReaderTests
{
    internal const string Chart = """
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
        [ExpertVocals]
        {
          0 = N 0 0
        }
        """;

    [TestMethod]
    public void ChartReadsTheSongTempoAndTracks()
    {
        var chart = ChartReader.Read(Encoding.UTF8.GetBytes(Chart), "notes.chart");

        Assert.AreEqual(TimeSpan.FromSeconds(0.25), chart.Offset);
        Assert.AreEqual(192, chart.Tempo.Resolution);
        Assert.AreEqual(2, chart.Tempo.SecondsAt(768), 1e-9);
        Assert.AreEqual(new TimeSignature(768, 6, 8), chart.Tempo.TimeSignatures[^1]);
        Assert.AreSequenceEqual(
            [ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums, ChartInstrument.GhlGuitar], [.. chart.Instruments]);
        Assert.AreSequenceEqual([ChartDifficulty.Hard], [.. chart.DifficultiesOf(ChartInstrument.Bass)]);
    }

    [TestMethod]
    public void ChartFretsHaveChordsSustainsHopoTapOpenAndStarPower()
    {
        var notes = Notes(ChartReader.Read(Encoding.UTF8.GetBytes(Chart), "notes.chart"), ChartInstrument.Guitar);

        Assert.AreSequenceEqual(
            [
                // A chord is a strum; star power covers ticks 0-299.
                new ChartNote(0, 96, 0, NoteFlags.StarPower),
                new ChartNote(0, 96, 1, NoteFlags.StarPower),
                // A different single note a beat after isn't close enough to be a HOPO.
                new ChartNote(192, 0, 2, NoteFlags.StarPower),
                // Close enough to be a natural HOPO, but forced, so a strum.
                new ChartNote(240, 0, 3, NoteFlags.StarPower),
                new ChartNote(480, 0, ChartNote.OpenLane, NoteFlags.None),
                new ChartNote(600, 0, 4, NoteFlags.Tap),
            ], notes);
    }

    [TestMethod]
    public void ChartDrumsHaveKicksDoubleKicksAndCymbals()
    {
        var notes = Notes(ChartReader.Read(Encoding.UTF8.GetBytes(Chart), "notes.chart"), ChartInstrument.Drums);

        Assert.AreSequenceEqual(
            [
                new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None),
                new ChartNote(0, 0, 1, NoteFlags.Cymbal),
                new ChartNote(48, 0, ChartNote.OpenLane, NoteFlags.DoubleKick),
                new ChartNote(96, 0, 2, NoteFlags.None),
                new ChartNote(192, 0, 3, NoteFlags.Cymbal),
            ], notes);
    }

    [TestMethod]
    public void ChartDoubleKickIsOnlyAnExpertNote()
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
              0 = N 32 0
            }
            [HardDrums]
            {
              0 = N 32 0
              96 = N 1 0
            }
            """);

        var read = ChartReader.Read(chart, "notes.chart");

        Assert.AreSequenceEqual([new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.DoubleKick)], Notes(read, ChartInstrument.Drums));
        Assert.AreSequenceEqual([new ChartNote(96, 0, 0, NoteFlags.None)], Notes(read, ChartInstrument.Drums, ChartDifficulty.Hard));
    }

    [TestMethod]
    public void ChartSixFretHasWhiteBlackAndOpen()
    {
        var notes = Notes(ChartReader.Read(Encoding.UTF8.GetBytes(Chart), "notes.chart"), ChartInstrument.GhlGuitar);

        Assert.AreSequenceEqual([0, 3, 5, ChartNote.OpenLane], [.. notes.Select(n => n.Lane)]);
    }

    [TestMethod]
    public void MidiReadsTheTempoAndTracks()
    {
        var chart = ChartReader.Read(SampleMidi(), "notes.mid");

        Assert.AreEqual(480, chart.Tempo.Resolution);
        Assert.AreEqual(2, chart.Tempo.SecondsAt(1920), 1e-9);
        Assert.IsNull(chart.Offset);
        Assert.AreSequenceEqual([ChartInstrument.Guitar, ChartInstrument.Drums], [.. chart.Instruments]);
        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Expert], [.. chart.DifficultiesOf(ChartInstrument.Guitar)]);
    }

    [TestMethod]
    public void MidiFretsHaveSustainsHopoTapOpenAndStarPower()
    {
        var notes = Notes(ChartReader.Read(SampleMidi(), "notes.mid"), ChartInstrument.Guitar);

        Assert.AreSequenceEqual(
            [
                // Held past a third of a beat, so a sustain; in star power.
                new ChartNote(0, 200, 0, NoteFlags.StarPower),
                new ChartNote(120, 0, 1, NoteFlags.Hopo),
                // Forced strum, though close enough to be a HOPO.
                new ChartNote(240, 0, 2, NoteFlags.None),
                new ChartNote(960, 0, 3, NoteFlags.Tap),
                new ChartNote(1440, 0, ChartNote.OpenLane, NoteFlags.None),
                // An open marked by Phase Shift sysex.
                new ChartNote(1920, 0, ChartNote.OpenLane, NoteFlags.None),
            ], notes);
    }

    [TestMethod]
    public void MidiDrumsAreCymbalsUnlessMarkedAsToms()
    {
        var notes = Notes(ChartReader.Read(SampleMidi(), "notes.mid"), ChartInstrument.Drums);

        Assert.AreSequenceEqual(
            [
                new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.DoubleKick),
                new ChartNote(0, 0, 1, NoteFlags.Cymbal),
                new ChartNote(480, 0, 1, NoteFlags.None),
                new ChartNote(480, 0, 3, NoteFlags.Cymbal),
            ], notes);
    }

    [TestMethod]
    public void MidiTomMarkersMarkOnlyTheirOwnPadOnEveryDifficulty()
    {
        // The blue tom marker, under yellow, blue and green on Expert (98-100) and Hard (86-88).
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Note(0, 111, 10).Note(0, 98).Note(0, 99).Note(0, 100).Note(0, 86).Note(0, 87).Note(0, 88)
            .Build();
        var chart = ChartReader.Read(midi, "notes.mid");

        foreach (var difficulty in new[] { ChartDifficulty.Expert, ChartDifficulty.Hard })
        {
            Assert.AreSequenceEqual(
                [
                    new ChartNote(0, 0, 1, NoteFlags.Cymbal),
                    new ChartNote(0, 0, 2, NoteFlags.None),
                    new ChartNote(0, 0, 3, NoteFlags.Cymbal),
                ], Notes(chart, ChartInstrument.Drums, difficulty));
        }
    }

    [TestMethod]
    public void MidiDrumsWithoutTomMarkersAreCymbalsUnlessProDrumsIsFalse()
    {
        // YARG takes a MIDI chart's drums as pro drums unless song.ini says they aren't.
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 98).Build();

        Assert.AreEqual(NoteFlags.Cymbal, Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums).Single().Flags);
        Assert.AreEqual(NoteFlags.None, Notes(ChartReader.Read(midi, "notes.mid", ChartReadOptions.From([new("pro_drums", "False")])), ChartInstrument.Drums).Single().Flags);
    }

    [TestMethod]
    public void ChartKickAndDoubleKickOnATickAreBothKept()
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
              0 = N 0 0
              0 = N 32 0
            }
            """);

        Assert.AreSequenceEqual(
            [new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None), new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.DoubleKick)], Notes(ChartReader.Read(chart, "notes.chart"), ChartInstrument.Drums));
    }

    [TestMethod]
    public void MidiNoteHeldForAThirdOfABeatIsASustainAsYargReadsIt()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96, 160).Note(480, 97, 159).Build();

        Assert.AreSequenceEqual([160, 0], [.. Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Guitar).Select(n => n.Length)]);
    }

    [TestMethod]
    public void MidiSustainCutoffComesFromSongIniWhereZeroIsATick()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96, 1).Note(480, 97, 100).Build();
        long[] Lengths(string cutoff) =>
            [.. Notes(ChartReader.Read(midi, "notes.mid", ChartReadOptions.From([new("sustain_cutoff_threshold", cutoff)])), ChartInstrument.Guitar).Select(n => n.Length)];

        Assert.AreSequenceEqual([0, 100], Lengths("100"));
        Assert.AreSequenceEqual([0, 0], Lengths("101"));
        Assert.AreSequenceEqual([1, 100], Lengths("0"));
        // A value that isn't a number is 0 to YARG, so a tick.
        Assert.AreSequenceEqual([1, 100], Lengths("soon"));
    }

    [TestMethod]
    public void ChartSustainsHaveACutoffOnlyWhenSongIniSetsOne()
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
            [ExpertSingle]
            {
              0 = N 0 10
              192 = N 1 64
            }
            """);
        long[] Lengths(ChartReadOptions options) => [.. Notes(ChartReader.Read(chart, "notes.chart", options), ChartInstrument.Guitar).Select(n => n.Length)];

        Assert.AreSequenceEqual([10, 64], Lengths(ChartReadOptions.Default));
        Assert.AreSequenceEqual([0, 64], Lengths(ChartReadOptions.From([new("sustain_cutoff_threshold", "64")])));
    }

    [TestMethod]
    public void HopoFrequencySetsTheNaturalHopoThreshold()
    {
        // Past the default threshold of each: 100 ticks apart in a .chart at 192, 200 in MIDI at 480.
        byte[] chart = Encoding.UTF8.GetBytes("""
            [Song]
            {
              Resolution = 192
            }
            [SyncTrack]
            {
              0 = B 120000
            }
            [ExpertSingle]
            {
              0 = N 0 0
              100 = N 1 0
            }
            """);
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Note(200, 97).Build();
        NoteFlags Second(byte[] bytes, string name, ChartReadOptions options) => Notes(ChartReader.Read(bytes, name, options), ChartInstrument.Guitar)[1].Flags;

        Assert.AreEqual(NoteFlags.None, Second(chart, "notes.chart", ChartReadOptions.Default));
        Assert.AreEqual(NoteFlags.Hopo, Second(chart, "notes.chart", ChartReadOptions.From([new("hopo_frequency", "100")])));
        Assert.AreEqual(NoteFlags.None, Second(midi, "notes.mid", ChartReadOptions.Default));
        // As YARG reads it, a MIDI chart's hopo_frequency has a tick of leniency.
        Assert.AreEqual(NoteFlags.Hopo, Second(midi, "notes.mid", ChartReadOptions.From([new("hopo_frequency", "199")])));
    }

    [TestMethod]
    public void MalformedChartsThrow()
    {
        Assert.ThrowsExactly<ChartFormatException>(() => ChartReader.Read([1, 2, 3], "notes.mid"));
        Assert.ThrowsExactly<ChartFormatException>(() => ChartReader.Read("no sections"u8.ToArray(), "notes.chart"));
        Assert.ThrowsExactly<ArgumentException>(() => ChartReader.Read([], "song.ogg"));
    }

    [TestMethod]
    public void PickPrefersTheMidi()
    {
        Assert.AreEqual("notes.mid", ChartReader.Pick(["notes.chart", "song.ogg", "notes.mid"]));
        Assert.AreEqual("Notes.Chart", ChartReader.Pick(["Notes.Chart"]));
        Assert.IsNull(ChartReader.Pick(["song.ogg"]));
    }

    [TestMethod]
    public void DelayIsTheSongIniDelayOrTheChartOffset()
    {
        var chart = ChartReader.Read(Encoding.UTF8.GetBytes(Chart), "notes.chart");

        Assert.AreEqual(TimeSpan.FromMilliseconds(-40), SongChartSettings.DelayOf([new("Delay", "-40")], chart));
        Assert.AreEqual(TimeSpan.FromSeconds(0.25), SongChartSettings.DelayOf([new("delay", "soon")], chart));
    }

    internal static ChartNote[] Notes(ChartDocument chart, ChartInstrument instrument, ChartDifficulty difficulty = ChartDifficulty.Expert) =>
        [.. chart.Tracks[new ChartTrackKey(instrument, difficulty)].Notes];

    /// <summary>120 BPM for four beats then 60 BPM; Expert and Hard guitar, and Expert pro drums.</summary>
    internal static byte[] SampleMidi() => new MidiBuilder()
        .Track("tempo").Tempo(0, 120).Tempo(1920, 60).TimeSignature(0, 4, 2)
        .Track("PART GUITAR")
        .Text(0, "[ENHANCED_OPENS]")
        .Note(0, 116, 100)
        .Note(0, 96, 200)
        .Note(120, 97)
        .Note(240, 98)
        .Note(240, 102, 10)
        .Note(960, 99)
        .Note(960, 104, 10)
        .Note(1440, 95)
        .PhaseShift(1910, 3, 1, true)
        .Note(1920, 96)
        .PhaseShift(1990, 3, 1, false)
        .Note(0, 84)
        .Track("PART DRUMS")
        .Note(0, 95)
        .Note(0, 98)
        .Note(480, 98)
        .Note(480, 110, 10)
        .Note(480, 100)
        .Track("PART VOCALS")
        .Note(0, 60)
        .Build();
}
