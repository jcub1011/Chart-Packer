using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class MidiTrackNotesTests
{
    [TestMethod]
    public void ConsumedHoldsOnlyTheEventsPairedAndRead()
    {
        var (bytes, events) = TrackOf(new MidiBuilder()
            .Track("PART GUITAR")                        // 0: the first event, never read
            .Raw(0, 0x90, 60, 100)                       // 1: paired
            .Raw(10, 0x90, 60, 90)                       // 2: a second note-on while held, ignored
            .Raw(20, 0x80, 60, 0)                        // 3: paired
            .Raw(30, 0x80, 61, 0)                        // 4: a note-off with nothing held
            .PhaseShift(40, 3, MidiLayout.SysexOpen, true)  // 5: paired
            .PhaseShift(50, 3, MidiLayout.SysexOpen, false) // 6: paired
            .Text(60, "[mix 3 drums0d]")                 // 7: read
            .Meta(70, 0x02, "x"u8.ToArray())             // 8: a copyright, not read
            .Meta(80, 0x7F, [1, 2])                      // 9: an unknown meta event
            .PhaseShift(90, 3, MidiLayout.SysexTap, false) // 10: a sysex off with nothing held
            .Raw(95, 0xF0, 7, 0x50, 0x53, 0, 0, 3, 1, 1) // 11: too short for a Phase Shift event
            .Raw(100, 0x90, 62, 1));                     // 12: never let go; 13 is the end of the track
        var track = MidiTrackNotes.Of(bytes, events, enhancedOpens: true);

        Assert.AreSequenceEqual([1, 3, 5, 6, 7], [.. track.Consumed.Order()]);
        Assert.AreSequenceEqual([new MidiNote(60, 0, 100, 0, 20, false, 1, 3)], track.Notes);
        Assert.AreSequenceEqual([new MidiSysexSpan(3, MidiLayout.SysexOpen, 40, 50, 5, 6)], track.Sysex);
        Assert.AreSequenceEqual([new MidiText(60, 0x01, "mix 3 drums0d", 7)], track.Texts);
    }

    [TestMethod]
    public void TheFirstEventIsNeverRead()
    {
        var (bytes, events) = TrackOf(new MidiBuilder()
            .Track()
            .Raw(0, 0x90, 60, 100)
            .Raw(10, 0x80, 60, 0));
        var track = MidiTrackNotes.Of(bytes, events, enhancedOpens: false);

        Assert.IsEmpty(track.Notes);
        Assert.IsEmpty(track.Consumed);
    }

    [TestMethod]
    public void NotesKeepTheirVelocityAndChannel()
    {
        var (bytes, events) = TrackOf(new MidiBuilder()
            .Track("PART DRUMS")
            .Note(0, 98, 10, velocity: 127)
            .Raw(5, 0x91, 98, 1)
            .Raw(15, 0x81, 98, 0));
        var track = MidiTrackNotes.Of(bytes, events, enhancedOpens: false);

        // Paired by pitch and channel, in the order they're let go.
        Assert.AreSequenceEqual(
            [new MidiNote(98, 0, 127, 0, 10, false, 1, 3), new MidiNote(98, 1, 1, 5, 15, false, 2, 4)],
            track.Notes);
    }

    [TestMethod]
    public void EnhancedOpensTurnsOnOpensForNotesLetGoAfterIt()
    {
        var builder = new MidiBuilder()
            .Track("PART GUITAR")
            .Note(0, 95, 4)
            .Text(5, "[ENHANCED_OPENS]")
            .Note(0, 96, 10);

        var (bytes, events) = TrackOf(builder);
        var fiveFret = MidiTrackNotes.Of(bytes, events, enhancedOpens: true);
        var other = MidiTrackNotes.Of(bytes, events, enhancedOpens: false);

        Assert.AreSequenceEqual([false, true], [.. fiveFret.Notes.Select(n => n.EnhancedOpens)]);
        Assert.AreSequenceEqual([false, false], [.. other.Notes.Select(n => n.EnhancedOpens)]);
        Assert.AreSequenceEqual(["ENHANCED_OPENS"], [.. fiveFret.Texts.Select(t => t.Text)]);
        Assert.Contains(fiveFret.Texts[0].Index, fiveFret.Consumed);
    }

    [TestMethod]
    public void SysexForTakesTheDifficultysPhrasesThenEveryDifficultys()
    {
        var (bytes, events) = TrackOf(new MidiBuilder()
            .Track("PART GUITAR")
            .PhaseShift(0, MidiLayout.SysexAllDifficulties, MidiLayout.SysexOpen, true)
            .PhaseShift(10, MidiLayout.SysexAllDifficulties, MidiLayout.SysexOpen, false)
            .PhaseShift(100, 3, MidiLayout.SysexOpen, true)
            .PhaseShift(110, 3, MidiLayout.SysexOpen, false)
            .PhaseShift(200, 3, MidiLayout.SysexOpen, true)
            .PhaseShift(210, 3, MidiLayout.SysexOpen, false)
            .PhaseShift(300, 3, MidiLayout.SysexTap, true)
            .PhaseShift(310, 3, MidiLayout.SysexTap, false));
        var track = MidiTrackNotes.Of(bytes, events, enhancedOpens: true);

        Assert.AreSequenceEqual(
            [new TickSpan(100, 110), new TickSpan(200, 210), new TickSpan(0, 10)],
            track.SysexFor(ChartDifficulty.Expert, MidiLayout.SysexOpen));
        Assert.AreSequenceEqual([new TickSpan(0, 10)], track.SysexFor(ChartDifficulty.Hard, MidiLayout.SysexOpen));
        Assert.AreSequenceEqual([new TickSpan(300, 310)], track.SysexFor(ChartDifficulty.Expert, MidiLayout.SysexTap));
    }

    [TestMethod]
    public void ATickSpanCoversItsStartToTheTickBeforeItsEnd()
    {
        Assert.IsFalse(new TickSpan(10, 20).Covers(9));
        Assert.IsTrue(new TickSpan(10, 20).Covers(10));
        Assert.IsTrue(new TickSpan(10, 20).Covers(19));
        Assert.IsFalse(new TickSpan(10, 20).Covers(20));
        Assert.IsTrue(new TickSpan(10, 10).Covers(10));
        Assert.IsFalse(new TickSpan(10, 10).Covers(11));
    }

    private static (byte[] Bytes, List<MidiEvent> Events) TrackOf(MidiBuilder builder)
    {
        byte[] bytes = builder.Build();
        var (_, _, chunks) = MidiFile.ReadChunks(bytes);
        return (bytes, MidiFile.ReadEvents(bytes, chunks[1]));
    }
}
