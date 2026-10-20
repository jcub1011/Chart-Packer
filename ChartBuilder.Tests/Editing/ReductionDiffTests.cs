using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Editing;

/// <summary>How a draft of Reduce differs from its source as the games read them, for the Reduce dialog's preview.</summary>
[TestClass]
public sealed class ReductionDiffTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Easy = ChartDifficulty.Easy;
    private const int Kick = ChartNote.OpenLane;

    private static readonly Func<long, ChordFlags> Unforced = static _ => ChordFlags.None;

    [TestMethod]
    public void TheSameTracksDifferInNothing()
    {
        var notes = Track(Note(0, 0), Note(0, 2), Note(480, 1, 240), Note(600, 3, flags: NoteFlags.Hopo));

        var diff = Compare(notes, Track([.. notes.Notes]));

        Assert.IsEmpty(diff.Changes);
        Assert.IsTrue(diff.SourceMarks.All(m => m == ReductionChanges.None));
        Assert.IsTrue(diff.DraftMarks.All(m => m == ReductionChanges.None));
    }

    [TestMethod]
    public void AChordTheDraftHasNoneOfIsRemovedAndEachOfItsNotesMarked()
    {
        var diff = Compare(Track(Note(0, 0), Note(480, 1), Note(480, 3), Note(960, 2)), Track(Note(0, 0), Note(960, 2)));

        Assert.AreSequenceEqual([new ReductionChange(480, 480, ReductionChanges.Removed, false)], diff.Changes.ToList());
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.Removed, ReductionChanges.Removed, ReductionChanges.None], diff.SourceMarks.ToList());
        Assert.IsTrue(diff.DraftMarks.All(m => m == ReductionChanges.None));
    }

    [TestMethod]
    public void AChordLeftWithFewerFretsIsReshapedTheLostFretMarkedOnTheSourceAndTheChordOnTheDraft()
    {
        // Green and blue to green.
        var diff = Compare(Track(Note(0, 0), Note(0, 3)), Track(Note(0, 0)));

        Assert.AreEqual(ReductionChanges.Reshaped, diff.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.Reshaped], diff.SourceMarks.ToList());
        Assert.AreSequenceEqual([ReductionChanges.Reshaped], diff.DraftMarks.ToList());
    }

    [TestMethod]
    public void AChordMovedDownIsReshaped()
    {
        // Green and orange to green and blue.
        var diff = Compare(Track(Note(0, 0), Note(0, 4)), Track(Note(0, 0), Note(0, 3)));

        Assert.AreEqual(ReductionChanges.Reshaped, diff.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.Reshaped], diff.SourceMarks.ToList());
        Assert.AreSequenceEqual([ReductionChanges.Reshaped, ReductionChanges.Reshaped], diff.DraftMarks.ToList());
    }

    [TestMethod]
    public void ANoteHeldShorterIsTrimmedAndOnlyItIsMarked()
    {
        var diff = Compare(Track(Note(0, 0, 480), Note(0, 1, 480)), Track(Note(0, 0, 480), Note(0, 1)));

        Assert.AreSequenceEqual([new ReductionChange(0, 480, ReductionChanges.SustainTrimmed, false)], diff.Changes.ToList());
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.SustainTrimmed], diff.DraftMarks.ToList());
        Assert.IsTrue(diff.SourceMarks.All(m => m == ReductionChanges.None));
    }

    [TestMethod]
    public void AReshapedChordHeldShorterThanItsLongestNoteWasIsTrimmed()
    {
        // Green and a held blue to green alone.
        var diff = Compare(Track(Note(0, 0), Note(0, 3, 960)), Track(Note(0, 0)));

        Assert.AreEqual(ReductionChanges.Reshaped | ReductionChanges.SustainTrimmed, diff.Changes.Single().Kinds);
        Assert.AreEqual(960L, diff.Changes.Single().EndTick);
    }

    [TestMethod]
    public void AChordReadAsAHopoWithoutItsForcingChangingIsANaturalFlip()
    {
        // A chord made one note close after another: a natural HOPO.
        var diff = Compare(Track(Note(0, 0), Note(120, 1), Note(120, 2)), Track(Note(0, 0), Note(120, 1, flags: NoteFlags.Hopo)));

        Assert.AreEqual(ReductionChanges.Reshaped | ReductionChanges.HopoFlipped, diff.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.Reshaped | ReductionChanges.HopoFlipped], diff.DraftMarks.ToList());
    }

    [TestMethod]
    public void AChordThatLosesItsForcedHopoIsAForcingChange()
    {
        var diff = ReductionDiff.Compare(Expert, Easy,
            Track(Note(0, 0), Note(480, 1, flags: NoteFlags.Hopo)), Track(Note(0, 0), Note(480, 1)),
            t => t == 480 ? ChordFlags.ForceHopo : ChordFlags.None, Unforced);

        Assert.AreEqual(ReductionChanges.ForcingChanged, diff.Changes.Single().Kinds);
    }

    [TestMethod]
    public void AChordThatLosesItsTapIsAForcingChange()
    {
        var diff = ReductionDiff.Compare(Expert, Easy,
            Track(Note(480, 1, flags: NoteFlags.Tap)), Track(Note(480, 1)),
            t => t == 480 ? ChordFlags.Tap : ChordFlags.None, Unforced);

        Assert.AreEqual(ReductionChanges.ForcingChanged, diff.Changes.Single().Kinds);
    }

    [TestMethod]
    public void AForcingChangeThatReadsTheSameIsNoChange()
    {
        // A forced strum on a chord that would be strummed anyway, left out.
        var diff = ReductionDiff.Compare(Expert, Easy, Track(Note(0, 0), Note(0, 1)), Track(Note(0, 0), Note(0, 1)),
            static _ => ChordFlags.ForceStrum, Unforced);

        Assert.IsEmpty(diff.Changes);
    }

    [TestMethod]
    public void InsideTheRangeTheDraftIsComparedWithTheSourceAndOutsideWithTheTargetAsItWas()
    {
        // Easy kept its own notes at 0 and 2880 while 960 to 1920 was reduced; its note at 2880 now reads as a HOPO, as the
        // draft put a note close before it.
        var source = Track(Note(0, 0), Note(960, 1), Note(1440, 2), Note(1920, 3), Note(2880, 4));
        var before = Track(Note(0, 2), Note(2880, 1));
        var draft = Track(Note(0, 2), Note(960, 1), Note(1920, 3), Note(2880, 1, flags: NoteFlags.Hopo));

        var diff = ReductionDiff.Compare(Expert, Easy, source, draft, Unforced, Unforced, (960, 1920), before);

        Assert.AreSequenceEqual(
            [new ReductionChange(1440, 1440, ReductionChanges.Removed, false), new ReductionChange(2880, 2880, ReductionChanges.HopoFlipped, true)],
            diff.Changes.ToList());
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.None, ReductionChanges.Removed, ReductionChanges.None, ReductionChanges.None], diff.SourceMarks.ToList());
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.None, ReductionChanges.None, ReductionChanges.HopoFlipped], diff.DraftMarks.ToList());
    }

    [TestMethod]
    public void WithoutTheTargetAsItWasNothingOutsideTheRangeIsCompared()
    {
        var diff = ReductionDiff.Compare(Expert, Easy, Track(Note(0, 0), Note(960, 1)), Track(Note(0, 4)), Unforced, Unforced, (960, 1920));

        Assert.AreSequenceEqual([960L], diff.Changes.Select(c => c.Tick).ToList());
    }

    [TestMethod]
    public void ADroppedLone2xKickIsOnlyThat()
    {
        var diff = Compare(Track(Note(0, Kick), Note(120, Kick, flags: NoteFlags.DoubleKick)), Track(Note(0, Kick)));

        Assert.AreEqual(ReductionChanges.DoubleKickDropped, diff.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.DoubleKickDropped], diff.SourceMarks.ToList());
    }

    [TestMethod]
    public void AKickAndA2xKickOnOneTickAreToldApart()
    {
        var diff = Compare(Track(Note(0, Kick, flags: NoteFlags.DoubleKick), Note(0, Kick), Note(0, 0)), Track(Note(0, Kick), Note(0, 0)));

        Assert.AreEqual(ReductionChanges.DoubleKickDropped, diff.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.DoubleKickDropped, ReductionChanges.None, ReductionChanges.None], diff.SourceMarks.ToList());
        Assert.IsTrue(diff.DraftMarks.All(m => m == ReductionChanges.None));
    }

    [TestMethod]
    public void AKickLeftOutOfAHitOrACymbalMadeATomIsReshaped()
    {
        var kickless = Compare(Track(Note(0, Kick), Note(0, 0)), Track(Note(0, 0)));
        var tom = Compare(Track(Note(0, 1, flags: NoteFlags.Cymbal)), Track(Note(0, 1)));

        Assert.AreEqual(ReductionChanges.Reshaped, kickless.Changes.Single().Kinds);
        Assert.AreEqual(ReductionChanges.Reshaped, tom.Changes.Single().Kinds);
        Assert.AreSequenceEqual([ReductionChanges.Reshaped], tom.SourceMarks.ToList());
    }

    [TestMethod]
    public void OnGhlAWhiteAndABlackFretOfOneColumnAreDifferentLanes()
    {
        // White 1 to black 1.
        var diff = Compare(Track(Note(0, 0)), Track(Note(0, 3)));

        Assert.AreEqual(ReductionChanges.Reshaped, diff.Changes.Single().Kinds);
    }

    [TestMethod]
    public void AChordOnlyTheDraftHasIsAdded()
    {
        var diff = Compare(Track(Note(0, 0)), Track(Note(0, 0), Note(480, 1)));

        Assert.AreEqual(new ReductionChange(480, 480, ReductionChanges.Added, false), diff.Changes.Single());
        Assert.AreSequenceEqual([ReductionChanges.None, ReductionChanges.Added], diff.DraftMarks.ToList());
    }

    [TestMethod]
    public void TheMarksAreOnePerNoteAndTheCountsAreByTick()
    {
        var diff = Compare(Track(Note(0, 0), Note(0, 1), Note(480, 2), Note(960, 3, 480)), Track(Note(0, 0), Note(960, 3)));

        Assert.HasCount(4, diff.SourceMarks);
        Assert.HasCount(2, diff.DraftMarks);
        Assert.AreEqual(1, diff.CountOf(ReductionChanges.Removed));
        Assert.AreEqual(2, diff.CountOf(ReductionChanges.Removed | ReductionChanges.Reshaped));
        Assert.AreEqual(1, diff.CountOf(ReductionChanges.SustainTrimmed));
    }

    private static ReductionDiff Compare(NoteTrack source, NoteTrack draft) => ReductionDiff.Compare(Expert, Easy, source, draft, Unforced, Unforced);

    private static NoteTrack Track(params ChartNote[] notes) => new(notes, []);

    private static ChartNote Note(long tick, int lane, long length = 0, NoteFlags flags = NoteFlags.None) => new(tick, length, lane, flags);
}
