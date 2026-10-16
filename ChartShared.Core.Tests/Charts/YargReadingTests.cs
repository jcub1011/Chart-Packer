using System.Text;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

/// <summary>Charts read as YARG reads them; each test names the YARG.Core behaviour it follows.</summary>
[TestClass]
public sealed class YargReadingTests
{
    [TestMethod]
    public void ChartNoteAfterAChordWithItIsANaturalHopoButMidiNoteIsAStrum()
    {
        // MoonNote.IsNaturalHopo: only MIDI makes a note contained in the chord before a strum.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              0 = N 1 0
              48 = N 0 0
            }
            """);
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Note(0, 97).Note(120, 96).Build();

        Assert.AreEqual(NoteFlags.Hopo, Notes(ChartReader.Read(chart, "notes.chart"))[^1].Flags);
        Assert.AreEqual(NoteFlags.None, Notes(ChartReader.Read(midi, "notes.mid"))[^1].Flags);
    }

    [TestMethod]
    public void HopoThresholdFallsBackAsYargsSongScanDoes()
    {
        static long Chart(string key, string value) => ChartReadOptions.From([new(key, value)]).HopoThreshold(192, chart: true);

        // A third of a beat, and for a .chart a tick more per 192 ticks of a beat.
        Assert.AreEqual(65L, ChartReadOptions.Default.HopoThreshold(192, chart: true));
        Assert.AreEqual(162L, ChartReadOptions.Default.HopoThreshold(480, chart: true));
        Assert.AreEqual(160L, ChartReadOptions.Default.HopoThreshold(480, chart: false));
        Assert.AreEqual(100L, Chart("hopo_frequency", "100"));
        Assert.AreEqual(65L, Chart("hopo_frequency", "0"));
        Assert.AreEqual(97L, Chart("eighthnote_hopo", "1"));
        Assert.AreEqual(65L, Chart("eighthnote_hopo", "0"));
        Assert.AreEqual(33L, Chart("hopofreq", "0"));
        Assert.AreEqual(193L, Chart("hopofreq", "5"));
        Assert.AreEqual(65L, Chart("hopofreq", "9"));

        // MIDI: YARG's tick of leniency first, then without it or Rock Band's 170/480 of a beat.
        Assert.AreSequenceEqual([161, 170], ChartReadOptions.Default.MidiHopoThresholds(480));
        Assert.AreSequenceEqual([101, 100], ChartReadOptions.From([new("hopo_frequency", "100")]).MidiHopoThresholds(480));
    }

    [TestMethod]
    public void MidiForceHopoWinsOverForceStrum()
    {
        // MidReader: a forced strum is skipped on a note already forced a HOPO, whichever comes first.
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR")
            .Note(0, 96).Note(960, 97).Note(960, 102, 10).Note(960, 101, 10)
            .Build();

        Assert.AreEqual(NoteFlags.Hopo, Notes(ChartReader.Read(midi, "notes.mid"))[1].Flags);
    }

    [TestMethod]
    public void StarPowerIsThePhraseThatStartedLast()
    {
        // MoonSongLoader keeps the latest phrase of each type, so one that starts inside another ends it.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              0 = S 2 1536
              768 = S 2 192
              1200 = N 1 0
            }
            """);

        Assert.AreSequenceEqual([NoteFlags.StarPower, NoteFlags.None], [.. Notes(ChartReader.Read(chart, "notes.chart")).Select(n => n.Flags)]);
    }

    [TestMethod]
    public void DuplicateChartNotesAreAChord()
    {
        // ChartReader pushes every note, so two greens at a tick are a chord, and the green after them can be a HOPO.
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              0 = N 0 0
              0 = N 0 0
              48 = N 0 0
            }
            """);

        Assert.AreSequenceEqual(
            [new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(0, 0, 0, NoteFlags.None), new ChartNote(48, 0, 0, NoteFlags.Hopo)], Notes(ChartReader.Read(chart, "notes.chart")));
    }

    [TestMethod]
    public void ChartLinesAreInTickOrderAndTypedByTheirFirstLetter()
    {
        byte[] chart = Chart("""
            [ExpertSingle]
            {
              192 = N 1 0
              0 = NOTE 0 0
              x = N 2 0
            }
            """);

        Assert.AreSequenceEqual([0, 1], [.. Notes(ChartReader.Read(chart, "notes.chart")).Select(n => n.Lane)]);
    }

    [TestMethod]
    public void ChartLinesAtATickKeepTheirFileOrderWhenSorted()
    {
        var events = ChartTextReader.TrackEvents(["192 = N 1 0", "0 = N 4 0", "0 = N 2 0", "0 = S 2 10", "96 = N 3 0"]);

        Assert.AreSequenceEqual(
            [(0L, 'N', 4), (0L, 'N', 2), (0L, 'S', 2), (96L, 'N', 3), (192L, 'N', 1)],
            [.. events.Select(e => (e.Tick, e.Type, e.Number))]);
    }

    [TestMethod]
    public void ChartMustStartWithSongThenSyncTrack()
    {
        byte[] noSync = Encoding.UTF8.GetBytes("[Song]\n{\n  Resolution = 192\n}\n[ExpertSingle]\n{\n  0 = N 0 0\n}\n");
        byte[] eventsFirst = Encoding.UTF8.GetBytes("[Song]\n{\n}\n[Events]\n{\n}\n[SyncTrack]\n{\n  0 = B 120000\n}\n");

        Assert.ThrowsExactly<ChartFormatException>(() => ChartReader.Read(noSync, "notes.chart"));
        Assert.ThrowsExactly<ChartFormatException>(() => ChartReader.Read(eventsFirst, "notes.chart"));
    }

    [TestMethod]
    public void ChartSectionNamesAreMatchedExactly()
    {
        byte[] chart = Chart("""
            [expertsingle]
            {
              0 = N 0 0
            }
            """);

        Assert.IsEmpty(ChartReader.Read(chart, "notes.chart").Tracks);
    }

    [TestMethod]
    public void FourLaneDrumsPlayTheFifthPadAsAGreenTom()
    {
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = N 5 0
              96 = N 4 0
            }
            """);

        var read = ChartReader.Read(chart, "notes.chart", ChartReadOptions.From([new("five_lane_drums", "False")]));

        Assert.IsFalse(read.IsFiveLaneDrums);
        Assert.AreSequenceEqual([new ChartNote(0, 0, 3, NoteFlags.None), new ChartNote(96, 0, 3, NoteFlags.None)], Notes(read, ChartInstrument.Drums));
    }

    [TestMethod]
    public void ChartDrumsAreLaidOutByWhicheverComesFirstOfAFifthPadAndACymbal()
    {
        byte[] cymbalFirst = Chart("""
            [ExpertDrums]
            {
              0 = N 2 0
              0 = N 66 0
              96 = N 5 0
            }
            """);
        byte[] fifthFirst = Chart("""
            [ExpertDrums]
            {
              0 = N 5 0
              96 = N 2 0
              96 = N 66 0
            }
            """);

        var pro = ChartReader.Read(cymbalFirst, "notes.chart");
        var five = ChartReader.Read(fifthFirst, "notes.chart");

        Assert.IsTrue(pro.IsProDrums);
        Assert.AreSequenceEqual([new ChartNote(0, 0, 1, NoteFlags.Cymbal), new ChartNote(96, 0, 3, NoteFlags.None)], Notes(pro, ChartInstrument.Drums));
        Assert.IsTrue(five.IsFiveLaneDrums);
        Assert.AreSequenceEqual([new ChartNote(0, 0, 4, NoteFlags.None), new ChartNote(96, 0, 1, NoteFlags.None)], Notes(five, ChartInstrument.Drums));
    }

    [TestMethod]
    public void MidiTomMarkerBeforeAFifthPadMakesProDrums()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 110, 10).Note(0, 98).Note(480, 101).Build();

        var read = ChartReader.Read(midi, "notes.mid");

        Assert.IsTrue(read.IsProDrums);
        Assert.AreSequenceEqual([new ChartNote(0, 0, 1, NoteFlags.None), new ChartNote(480, 0, 3, NoteFlags.None)], Notes(read, ChartInstrument.Drums));
    }

    [TestMethod]
    public void MidiTomMarkersToggleSoTwoMakeACymbal()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Note(0, 110, 10).Note(0, 110, 10).Note(0, 98).Build();
        byte[] twoTracks = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS").Raw(0, 0x90, 110, 100).Raw(0, 0x91, 110, 100).Note(0, 98).Raw(10, 0x80, 110, 0).Raw(10, 0x81, 110, 0).Build();

        // The second marker of a pitch and channel is a duplicate; on another channel it's a second marker.
        Assert.AreEqual(NoteFlags.None, Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums).Single().Flags);
        Assert.AreEqual(NoteFlags.Cymbal, Notes(ChartReader.Read(twoTracks, "notes.mid"), ChartInstrument.Drums).Single().Flags);
    }

    [TestMethod]
    public void DiscoFlipSwapsRedAndYellowOnProDrums()
    {
        byte[] chart = Chart("""
            [ExpertDrums]
            {
              0 = E mix 3 drums0d
              0 = N 1 0
              96 = N 2 0
              96 = N 66 0
              192 = E mix 3 drums0
              192 = N 1 0
            }
            """);
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART DRUMS")
            .Text(0, "[mix 3 drums0d]").Note(0, 97).Note(480, 98).Text(960, "[mix 3 drums0]").Note(960, 97)
            .Build();
        var flipped = new[] { new ChartNote(0, 0, 1, NoteFlags.Cymbal), new ChartNote(96, 0, 0, NoteFlags.None), new ChartNote(192, 0, 0, NoteFlags.None) };

        Assert.AreSequenceEqual(flipped, Notes(ChartReader.Read(chart, "notes.chart"), ChartInstrument.Drums));
        Assert.AreSequenceEqual([.. flipped.Select(n => n with { Tick = n.Tick * 5 })], Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Drums));
    }

    [TestMethod]
    public void MidiTrackNamesAreExactAndAtTheStart()
    {
        byte[] lower = new MidiBuilder().Track().Tempo(0, 120).Track("part guitar").Note(0, 96).Build();
        byte[] late = new MidiBuilder().Track().Tempo(0, 120).Track().Meta(10, 0x03, Encoding.ASCII.GetBytes("PART GUITAR")).Note(20, 96).Build();
        byte[] realDrums = new MidiBuilder().Track().Tempo(0, 120).Track("PART REAL_DRUMS_PS").Note(0, 96).Build();

        Assert.IsEmpty(ChartReader.Read(lower, "notes.mid").Tracks);
        Assert.IsEmpty(ChartReader.Read(late, "notes.mid").Tracks);
        Assert.AreEqual(ChartInstrument.Drums, ChartReader.Read(realDrums, "notes.mid").Instruments.Single());
    }

    [TestMethod]
    public void LaterPartGuitarReplacesAnEarlierTrack()
    {
        byte[] two = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Track("PART GUITAR").Note(0, 97).Build();
        byte[] gems = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Track("T1 GEMS").Note(0, 97).Build();

        Assert.AreEqual(1, Notes(ChartReader.Read(two, "notes.mid")).Single().Lane);
        Assert.AreEqual(0, Notes(ChartReader.Read(gems, "notes.mid")).Single().Lane);
    }

    [TestMethod]
    public void MidiNotesPairByChannelAndADuplicateNoteOnIsIgnored()
    {
        // A second note-on while one is held is dropped, so the first note lasts to the first note-off.
        byte[] duplicate = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 96, 200).Note(100, 96, 200).Build();
        // The note-off on channel 2 doesn't end the note on channel 1.
        byte[] channels = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Raw(0, 0x90, 96, 100).Raw(60, 0x81, 96, 0).Raw(200, 0x80, 96, 0).Build();
        // A note never let go isn't a note.
        byte[] held = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Raw(0, 0x90, 96, 100).Note(480, 97).Build();

        Assert.AreEqual(new ChartNote(0, 200, 0, NoteFlags.None), Notes(ChartReader.Read(duplicate, "notes.mid")).Single());
        Assert.AreEqual(200, Notes(ChartReader.Read(channels, "notes.mid")).Single().Length);
        Assert.AreEqual(1, Notes(ChartReader.Read(held, "notes.mid")).Single().Lane);
    }

    [TestMethod]
    public void PhaseShiftOpenPhraseMakesEveryNoteInItOpen()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR")
            .PhaseShift(0, 3, 1, true).Note(10, 96).Note(10, 97).PhaseShift(100, 3, 1, false)
            .Build();

        Assert.AreSequenceEqual([ChartNote.OpenLane, ChartNote.OpenLane], [.. Notes(ChartReader.Read(midi, "notes.mid")).Select(n => n.Lane)]);
    }

    [TestMethod]
    public void EnhancedOpensTakesEffectFromItsEventAndIsExact()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Note(0, 95).Text(100, "[ENHANCED_OPENS]").Note(200, 95).Build();
        byte[] lower = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Text(0, "[enhanced_opens]").Note(200, 95).Note(480, 96).Build();

        Assert.AreEqual(new ChartNote(200, 0, ChartNote.OpenLane, NoteFlags.None), Notes(ChartReader.Read(midi, "notes.mid")).Single());
        Assert.AreEqual(0, Notes(ChartReader.Read(lower, "notes.mid")).Single().Lane);
    }

    [TestMethod]
    public void TempoMapIsTheFirstTracksAsYargCountsIt()
    {
        // MidReader.ReadSync counts the first event's delta twice, so a tempo track that starts at 10 is 10 ticks late.
        byte[] other = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR").Tempo(480, 60).Note(0, 96).Build();
        byte[] late = new MidiBuilder().Track().Tempo(10, 120).Tempo(480, 60).Track("PART GUITAR").Note(0, 96).Build();

        Assert.DoesNotContain(t => t.Tick == 480, ChartReader.Read(other, "notes.mid").Tempo.Tempos);
        Assert.Contains(t => t.Tick == 490, ChartReader.Read(late, "notes.mid").Tempo.Tempos);
    }

    [TestMethod]
    public void MultiplierNote103PutsStarPowerOn103()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR")
            .Note(0, 103, 480).Note(0, 96).Note(960, 116, 100).Note(960, 97)
            .Build();

        var flags103 = Notes(ChartReader.Read(midi, "notes.mid", ChartReadOptions.From([new("multiplier_note", "103")]))).Select(n => n.Flags);
        var flags116 = Notes(ChartReader.Read(midi, "notes.mid")).Select(n => n.Flags);

        Assert.AreSequenceEqual([NoteFlags.StarPower, NoteFlags.None], [.. flags103]);
        Assert.AreSequenceEqual([NoteFlags.None, NoteFlags.StarPower], [.. flags116]);
    }

    [TestMethod]
    public void SongIniValuesAreReadLeniently()
    {
        Assert.AreEqual(64L, SongIniValues.Integer("64.5"));
        Assert.AreEqual(7L, SongIniValues.Integer("+7"));
        Assert.AreEqual(0L, SongIniValues.Integer("soon"));
        Assert.IsNull(SongIniValues.Integer(null));
        Assert.AreEqual(long.MaxValue, SongIniValues.Integer("99999999999999999999999"));
        Assert.AreEqual(int.MaxValue, SongIniValues.Integer("99999999999", int.MinValue, int.MaxValue));
        Assert.IsTrue(SongIniValues.Boolean("10"));
        Assert.IsTrue(SongIniValues.Boolean("TRUEish"));
        Assert.IsFalse(SongIniValues.Boolean("yes"));
        Assert.AreEqual(0.5, SongIniValues.Real(".5"));
        Assert.AreEqual(-1.25, SongIniValues.Real("-1.25s"));
        Assert.AreEqual("False", SongIniValues.Last([new("pro_drums", "True"), new("PRO_DRUM", "False")], "pro_drum", "pro_drums"));
    }

    [TestMethod]
    public void DelayOfZeroFallsBackToDelaySecondsThenTheChartOffset()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), SongChartSettings.DelayOf([new("delay", "0"), new("delay_seconds", "0.5")], (TimeSpan?)null));
        Assert.AreEqual(TimeSpan.FromSeconds(0.25), SongChartSettings.DelayOf([new("delay", "0")], TimeSpan.FromSeconds(0.25)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), SongChartSettings.DelayOf([new("delay", "20")], TimeSpan.FromSeconds(0.25)));
    }

    [TestMethod]
    public void NegativeSustainCutoffIsTheDefault()
    {
        Assert.AreEqual(160L, SustainCutoff.Of(480, -5));
        Assert.AreEqual(1L, SustainCutoff.Of(480, 0));
    }

    private static ChartNote[] Notes(ChartDocument chart, ChartInstrument instrument = ChartInstrument.Guitar) => ChartReaderTests.Notes(chart, instrument);

    private static byte[] Chart(string tracks) => Encoding.UTF8.GetBytes($$"""
        [Song]
        {
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = B 120000
        }
        {{tracks}}
        """);
}
