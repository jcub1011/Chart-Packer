using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The session keeping where <c>[music_start]</c>, <c>[music_end]</c> and <c>[end]</c> go (charter-spec C8 item 5) up to
/// date as the chart, the audio heard and the delay change.
/// </summary>
[TestClass]
public sealed class SongEventMarksTests
{
    // At 480 ticks a beat, 120 BPM and 4/4: a second is 960 ticks, and a measure 1920.

    [TestMethod]
    public void TheMarksFollowEachStepDoneUndoneAndRedone()
    {
        var session = Of(Guitar());
        int placed = 0;
        session.SongEventsPlaced += (_, _) => placed++;
        Assert.AreEqual(new SongEventMarks(0, 0, 1920, FromAudio: false), session.SongEventMarks);

        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(4000, 1, 0)]));
        Assert.AreEqual(new SongEventMarks(3840, 4000, 5760, FromAudio: false), session.SongEventMarks);

        session.Undo();
        Assert.AreEqual(new SongEventMarks(0, 0, 1920, FromAudio: false), session.SongEventMarks);

        session.Redo();
        Assert.AreEqual(new SongEventMarks(3840, 4000, 5760, FromAudio: false), session.SongEventMarks);
        Assert.AreEqual(3, placed);
    }

    [TestMethod]
    public void AStepThatMovesNothingSaysNothing()
    {
        var session = Of(Guitar());
        int placed = 0;
        session.SongEventsPlaced += (_, _) => placed++;

        session.Execute(new AddTextEvent(null, TextEvent.Of(960, "[section Intro]")));

        Assert.AreEqual(0, placed);
    }

    [TestMethod]
    public void TheMarksFollowTheAudioHeardAndTheDelay()
    {
        var session = Of(Guitar());
        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(1920, 1, 0)]));
        int placed = 0;
        session.SongEventsPlaced += (_, _) => placed++;

        session.SetAudible(new AudibleRange(1, 3));
        Assert.AreEqual(new SongEventMarks(960, 2880, 3840, FromAudio: true), session.SongEventMarks);
        session.SetAudible(new AudibleRange(1, 3));
        Assert.AreEqual(1, placed, "The same range again moves nothing.");

        session.SetSongSettings([], TimeSpan.FromSeconds(0.5));
        Assert.AreEqual(new SongEventMarks(480, 2400, 3840, FromAudio: true), session.SongEventMarks);

        session.SetAudible(null);
        Assert.AreEqual(new SongEventMarks(1920, 1920, 3840, FromAudio: false), session.SongEventMarks);
        Assert.AreEqual(3, placed);
    }

    [TestMethod]
    public void AGesturesPreviewIsntPlacedUntilItsCommitted()
    {
        var session = Of(Guitar());
        int placed = 0;
        session.SongEventsPlaced += (_, _) => placed++;

        session.Preview(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(4000, 1, 0)]));
        Assert.AreEqual(0, placed);
        Assert.AreEqual(1920, session.SongEventMarks.End);

        session.CommitPreview();
        Assert.AreEqual(1, placed);
        Assert.AreEqual(5760, session.SongEventMarks.End);
    }

    [TestMethod]
    public void VocalsCountAsNotes()
    {
        var session = Of(WithVocals([new VocalNoteEvent(960, 240, 60), new VocalNoteEvent(5000, 480, 62)]));

        Assert.AreEqual(new SongEventMarks(0, 5480, 5760, FromAudio: false), session.SongEventMarks);
    }

    [TestMethod]
    public void UndoingPastEditsThatMovedTheMarksLeavesTheEventsAsTheyWere()
    {
        // The marks aren't in the chart, so moving them shifts nothing an undo takes back by its place.
        var session = Of(Guitar());
        var section = TextEvent.Of(960, "[section Intro]");
        session.Execute(new AddTextEvent(null, section));
        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(9000, 1, 0)]));
        session.Execute(new AddTextEvent(null, TextEvent.Of(9600, "[crowd_normal]")));

        session.Undo();
        session.Undo();
        Assert.AreSequenceEqual([section], session.Chart.Events.ToList());
        session.Undo();

        Assert.IsEmpty(session.Chart.Events);
        Assert.AreEqual(new SongEventMarks(0, 0, 1920, FromAudio: false), session.SongEventMarks);
    }
}
