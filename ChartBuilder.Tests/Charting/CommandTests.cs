using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>Each note, phrase and text event command: what it does, that it reverts and redoes exactly, and what it refuses.</summary>
[TestClass]
public sealed class CommandTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    // Adding notes.

    [TestMethod]
    public void AddNotesAddsEachNoteAndRevertsAndRedoesExactly()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddNotes(Guitar, Expert, [new EditableNote(960, 1, 1), new EditableNote(960, 240, 3)]);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.IsTrue(after.PartOf(Guitar)![Expert].TryGetNote(960, 3, false, out var note));
        Assert.AreEqual(240, note.Length);
        Assert.AreSequenceEqual([new ChartLocation(Guitar, Expert, 960, 1200)], command.Affected.Regions);
        Assert.AreEqual(SongChanges.None, command.Affected.Song);
    }

    [TestMethod]
    public void AddNotesRefusesAPlaceThatHasANoteAndChangesNothing()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new AddNotes(Guitar, Expert, [new EditableNote(960, 1, 1), new EditableNote(480, 5, 2)]));
    }

    [TestMethod]
    public void AddNotesRefusesALaneThePartHasNot()
    {
        var chart = EditableChartSamples.Full();

        Refuses<ArgumentException>(chart, new AddNotes(Guitar, Expert, [new EditableNote(960, 1, 1), new EditableNote(960, 1, 5)]));
    }

    [TestMethod]
    public void AddNotesRefusesAPartTheChartHasNot()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new AddNotes(ChartInstrument.Bass, Expert, [new EditableNote(0, 1, 1)]));
    }

    [TestMethod]
    public void NoteCommandsRefuseNoNotesOrTwoNotesAtOnePlace()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AddNotes(Guitar, Expert, []));
        Assert.ThrowsExactly<ArgumentException>(() => new RemoveNotes(Guitar, Expert, [new EditableNote(0, 1, 1), new EditableNote(0, 9, 1)]));
    }

    [TestMethod]
    public void ACommandCanNotBeAppliedTwiceOrRevertedWhenNotApplied()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddNotes(Guitar, Expert, [new EditableNote(960, 1, 1)]);

        Assert.ThrowsExactly<InvalidOperationException>(() => command.Revert(chart));
        command.Apply(chart);
        Assert.ThrowsExactly<InvalidOperationException>(() => command.Apply(chart));
        Assert.ThrowsExactly<InvalidOperationException>(() => command.Revert(chart.Clone()));
    }

    // Removing notes.

    [TestMethod]
    public void RemoveNotesRemovesEachNoteAndRevertsAndRedoesExactly()
    {
        var chart = EditableChartSamples.Full();
        var command = new RemoveNotes(Drums, Expert, [new EditableNote(0, 1, 0, DoubleKick: true), new EditableNote(480, 1, 3, NoteDynamics.Ghost)]);

        var after = AppliesRevertsAndRedoes(chart, command);

        var notes = after.PartOf(Drums)![Expert].Notes;
        Assert.AreSequenceEqual([new EditableNote(0, 1, 0), new EditableNote(0, 1, 2, NoteDynamics.Accent)], notes.ToList());
        Assert.AreSequenceEqual([new ChartLocation(Drums, Expert, 0, 1), new ChartLocation(Drums, Expert, 480, 481)], command.Affected.Regions);
    }

    [TestMethod]
    public void RemovingTheLastNotesOfAChordClearsItsMarkingAndRevertingRestoresIt()
    {
        var chart = EditableChartSamples.Full();
        var command = new RemoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2), new EditableNote(480, 1, ChartNote.OpenLane)]);

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.IsEmpty(after.PartOf(Guitar)![Expert].FlaggedChords);
        command.Revert(chart);
        Assert.AreEqual(ChordFlags.ForceHopo, chart.PartOf(Guitar)![Expert].FlagsAt(480));
    }

    [TestMethod]
    public void RemovingSomeNotesOfAChordKeepsItsMarking()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new RemoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2)]));

        Assert.AreEqual(ChordFlags.ForceHopo, after.PartOf(Guitar)![Expert].FlagsAt(480));
    }

    [TestMethod]
    public void RemoveNotesRefusesANoteTheTrackHasNotExactly()
    {
        var chart = EditableChartSamples.Full();

        // The track's note at 480 in lane 2 is 240 long.
        Refuses<InvalidOperationException>(chart, new RemoveNotes(Guitar, Expert, [new EditableNote(0, 1, 0), new EditableNote(480, 1, 2)]));
    }

    // Moving notes.

    [TestMethod]
    public void MovingEveryNoteOfAChordTakesItsMarkingWithIt()
    {
        var chart = EditableChartSamples.Full();
        var command = new MoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2), new EditableNote(480, 1, ChartNote.OpenLane)], 480, 0);

        var after = AppliesRevertsAndRedoes(chart, command);

        var track = after.PartOf(Guitar)![Expert];
        Assert.AreSequenceEqual([new EditableNote(0, 1, 0), new EditableNote(960, 1, ChartNote.OpenLane), new EditableNote(960, 240, 2)], track.Notes.ToList());
        Assert.AreSequenceEqual([new FlaggedChord(960, ChordFlags.ForceHopo)], track.FlaggedChords.ToList());
        Assert.AreSequenceEqual([new EditableNote(960, 240, 2), new EditableNote(960, 1, ChartNote.OpenLane)], command.Moved);
        Assert.AreSequenceEqual([new ChartLocation(Guitar, Expert, 480, 720), new ChartLocation(Guitar, Expert, 960, 1200)], command.Affected.Regions);
    }

    [TestMethod]
    public void MovingSomeNotesOfAChordLeavesItsMarking()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2)], 480, 0));

        var track = after.PartOf(Guitar)![Expert];
        Assert.AreSequenceEqual([new FlaggedChord(480, ChordFlags.ForceHopo)], track.FlaggedChords.ToList());
        Assert.AreEqual(ChordFlags.None, track.FlagsAt(960));
    }

    [TestMethod]
    public void AMovedChordsMarkingIsAddedToTheMarkingOfTheChordItJoins()
    {
        var chart = NewChart(Guitar);
        var track = chart.PartOf(Guitar)![Expert];
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.SetFlags(0, ChordFlags.Tap);
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.Notes.Add(new EditableNote(480, 1, 2));
        track.SetFlags(480, ChordFlags.ForceHopo);

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 1, 1), new EditableNote(480, 1, 2)], -480, 0));

        Assert.AreSequenceEqual([new FlaggedChord(0, ChordFlags.ForceHopo | ChordFlags.Tap)], after.PartOf(Guitar)![Expert].FlaggedChords.ToList());
    }

    [TestMethod]
    public void AChordMovingOntoATickWhoseChordAlsoMovesAwayTakesOnlyItsOwnMarking()
    {
        var chart = NewChart(Guitar);
        var track = chart.PartOf(Guitar)![Expert];
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.SetFlags(0, ChordFlags.Tap);
        track.Notes.Add(new EditableNote(480, 1, 1));
        track.SetFlags(480, ChordFlags.ForceHopo);

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [new EditableNote(0, 1, 0), new EditableNote(480, 1, 1)], 480, 0));

        Assert.AreSequenceEqual([new FlaggedChord(480, ChordFlags.Tap), new FlaggedChord(960, ChordFlags.ForceHopo)], after.PartOf(Guitar)![Expert].FlaggedChords.ToList());
    }

    [TestMethod]
    public void NotesCanMoveIntoPlacesTheNotesMovingLeave()
    {
        var chart = NewChart(Guitar);
        var track = chart.PartOf(Guitar)![Expert];
        track.Notes.Add(new EditableNote(0, 1, 0));
        track.Notes.Add(new EditableNote(0, 1, 1));

        var after = AppliesRevertsAndRedoes(chart, new MoveNotes(Guitar, Expert, [new EditableNote(0, 1, 0), new EditableNote(0, 1, 1)], 0, 1));

        Assert.AreSequenceEqual([new EditableNote(0, 1, 1), new EditableNote(0, 1, 2)], after.PartOf(Guitar)![Expert].Notes.ToList());
    }

    [TestMethod]
    public void MoveNotesRefusesAPlaceANoteThatIsNotMovingHas()
    {
        var chart = EditableChartSamples.Full();

        // The yellow moves down two lanes onto the green at 0.
        Refuses<InvalidOperationException>(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2)], -480, -2));
    }

    [TestMethod]
    public void MoveNotesRefusesAMoveThatChangesWhatANoteIs()
    {
        var chart = EditableChartSamples.Full();

        // A fret moving down onto the open lane, an open moving up, a pad moving down onto the kick, and a kick moving up.
        Refuses<ArgumentException>(chart, new MoveNotes(Guitar, Expert, [new EditableNote(0, 1, 0)], 0, -1));
        Refuses<ArgumentException>(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 1, ChartNote.OpenLane)], 960, 1));
        Refuses<ArgumentException>(chart, new MoveNotes(Drums, Expert, [new EditableNote(0, 1, 2, NoteDynamics.Accent)], 960, -2));
        Refuses<ArgumentException>(chart, new MoveNotes(Drums, Expert, [new EditableNote(0, 1, 0)], 960, 1));
    }

    [TestMethod]
    public void MoveNotesRefusesALaneThePartHasNot()
    {
        var chart = EditableChartSamples.Full();

        Refuses<ArgumentException>(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2)], 0, 3));
        Refuses<ArgumentException>(chart, new MoveNotes(Drums, Expert, [new EditableNote(0, 1, 0, DoubleKick: true)], 480, 1));
    }

    [TestMethod]
    public void MoveNotesRefusesAMoveBeforeTheStart()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new MoveNotes(Guitar, Expert, [new EditableNote(480, 240, 2)], -481, 0));
    }

    // Sustains, dynamics and chord markings.

    [TestMethod]
    public void SetSustainGivesEachNoteItsLengthAndRevertsExactly()
    {
        var chart = EditableChartSamples.Full();
        var same = new SetSustain(Guitar, Expert, [new EditableNote(0, 1, 0), new EditableNote(480, 240, 2)], 960);
        var each = new SetSustain(Guitar, Expert, [(new EditableNote(0, 1, 0), 120L), (new EditableNote(480, 240, 2), 1L)]);

        var after = AppliesRevertsAndRedoes(chart, same);
        same.Revert(chart);
        var afterEach = AppliesRevertsAndRedoes(chart, each);

        Assert.AreSequenceEqual([new EditableNote(0, 960, 0), new EditableNote(480, 1, ChartNote.OpenLane), new EditableNote(480, 960, 2)], after.PartOf(Guitar)![Expert].Notes.ToList());
        Assert.AreSequenceEqual([new EditableNote(0, 120, 0), new EditableNote(480, 1, ChartNote.OpenLane), new EditableNote(480, 1, 2)], afterEach.PartOf(Guitar)![Expert].Notes.ToList());
        Assert.AreSequenceEqual([new ChartLocation(Guitar, Expert, 0, 1440)], same.Affected.Regions);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SetSustain(Guitar, Expert, [new EditableNote(0, 1, 0)], -1));
    }

    [TestMethod]
    public void SetDynamicsMakesDrumNotesAccentsOrGhostsAndRevertsExactly()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new SetDynamics(Drums, Expert, [new EditableNote(0, 1, 2, NoteDynamics.Accent), new EditableNote(480, 1, 3, NoteDynamics.Ghost)], NoteDynamics.Ghost));

        Assert.IsTrue(after.PartOf(Drums)![Expert].TryGetNote(0, 2, false, out var yellow));
        Assert.IsTrue(after.PartOf(Drums)![Expert].TryGetNote(480, 3, false, out var blue));
        Assert.AreEqual((NoteDynamics.Ghost, NoteDynamics.Ghost), (yellow.Dynamics, blue.Dynamics));
    }

    [TestMethod]
    public void SetDynamicsRefusesAKickWhichTheGamesAlwaysPlayAsANormalHit()
    {
        var chart = EditableChartSamples.Full();

        Refuses<ArgumentException>(chart, new SetDynamics(Drums, Expert, [new EditableNote(0, 1, 0), new EditableNote(0, 1, 2, NoteDynamics.Accent)], NoteDynamics.Ghost));
        Refuses<ArgumentException>(chart, new SetDynamics(Drums, Expert, [new EditableNote(0, 1, 0, DoubleKick: true)], NoteDynamics.Accent));
    }

    [TestMethod]
    public void SetDynamicsRefusesNotesThatAreNotDrums()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new SetDynamics(Guitar, Expert, [new EditableNote(0, 1, 0)], NoteDynamics.Accent));
    }

    [TestMethod]
    public void SetChordFlagsTurnsAMarkingOnOrOffLeavingTheOthers()
    {
        var chart = EditableChartSamples.Full();
        var on = new SetChordFlags(Guitar, Expert, [0, 480], ChordFlags.Tap, on: true);
        var off = new SetChordFlags(Guitar, Expert, [480], ChordFlags.ForceHopo, on: false);

        var afterOn = AppliesRevertsAndRedoes(chart, on);
        on.Revert(chart);
        var afterOff = AppliesRevertsAndRedoes(chart, off);

        Assert.AreSequenceEqual([new FlaggedChord(0, ChordFlags.Tap), new FlaggedChord(480, ChordFlags.ForceHopo | ChordFlags.Tap)], afterOn.PartOf(Guitar)![Expert].FlaggedChords.ToList());
        Assert.IsEmpty(afterOff.PartOf(Guitar)![Expert].FlaggedChords);
        Assert.AreSequenceEqual([new ChartLocation(Guitar, Expert, 0, 0), new ChartLocation(Guitar, Expert, 480, 480)], on.Affected.Regions);
    }

    [TestMethod]
    public void SetChordFlagsRefusesATickWithoutNotesAndDrums()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new SetChordFlags(Guitar, Expert, [0, 240], ChordFlags.Tap, on: true));
        Refuses<InvalidOperationException>(chart, new SetChordFlags(Drums, Expert, [0], ChordFlags.ForceHopo, on: true));
        Assert.ThrowsExactly<ArgumentException>(() => new SetChordFlags(Guitar, Expert, [0], ChordFlags.None, on: true));
    }

    // Phrases.

    [TestMethod]
    public void AddPhraseAddsItForEveryDifficultyAndRevertsExactly()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 1920, 480));

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.Contains(new Phrase(PhraseKind.StarPower, 1920, 480), after.PartOf(Guitar)!.Phrases.ToList());
        Assert.AreSequenceEqual([new ChartLocation(Guitar, null, 1920, 2400)], command.Affected.Regions);
    }

    [TestMethod]
    public void AddPhraseRefusesADrumsPhraseOnAnotherPart()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new AddPhrase(Guitar, new Phrase(PhraseKind.DrumFill, 1920, 480)));
        Refuses<ArgumentException>(chart, new AddPhrase(Drums, new Phrase(PhraseKind.Roll, 1920, 480)));
    }

    [TestMethod]
    public void RemovePhraseRemovesItAndRevertsExactly()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new RemovePhrase(Drums, new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Hard)));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 1920, 480)], after.PartOf(Drums)!.Phrases.ToList());
        Refuses<InvalidOperationException>(chart, new RemovePhrase(Drums, new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Easy)));
    }

    [TestMethod]
    public void ChangePhraseChangesItsStartLengthAndLowestDifficulty()
    {
        var chart = EditableChartSamples.Full();
        var command = new ChangePhrase(Drums, new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Hard), new Phrase(PhraseKind.Roll, 2400, 240, ChartDifficulty.Easy));

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 1920, 480), new Phrase(PhraseKind.Roll, 2400, 240, ChartDifficulty.Easy)], after.PartOf(Drums)!.Phrases.ToList());
        Assert.AreSequenceEqual([new ChartLocation(Drums, null, 960, 1440), new ChartLocation(Drums, null, 2400, 2640)], command.Affected.Regions);
        Assert.ThrowsExactly<ArgumentException>(() => new ChangePhrase(Drums, new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Hard), new Phrase(PhraseKind.Trill, 960, 480, ChartDifficulty.Hard)));
    }

    // Text events.

    [TestMethod]
    public void AddTextEventGoesAfterTheEventsAtItsTickAndUndoingRemovesThatOne()
    {
        var chart = EditableChartSamples.Full();
        var command = new AddTextEvent(null, TextEvent.Of(0, "[music_start]"));

        var after = AppliesRevertsAndRedoes(chart, command);

        Assert.AreSequenceEqual(["[music_start]", "[end]", "[music_start]"], after.Events.At(0).ToArray().Select(e => e.Text));
        Assert.AreEqual(SongChanges.Events, command.Affected.Song);
        Assert.IsEmpty(command.Affected.Regions);
    }

    [TestMethod]
    public void RemoveTextEventPutsItBackInItsPlaceAmongTheEventsAtItsTick()
    {
        var chart = EditableChartSamples.Full();

        var after = AppliesRevertsAndRedoes(chart, new RemoveTextEvent(null, TextEvent.Of(0, "[music_start]")));

        // Reverting put [music_start] back before [end], as the comparer checks the order of the events at a tick.
        Assert.AreSequenceEqual(["[end]"], after.Events.At(0).ToArray().Select(e => e.Text));
    }

    [TestMethod]
    public void TextEventCommandsWorkOnAPartsTrack()
    {
        var chart = EditableChartSamples.Full();
        var add = new AddTextEvent(Guitar, TextEvent.Of(240, "[play]"));
        var remove = new RemoveTextEvent(Guitar, TextEvent.Of(0, "[idle]"));

        var afterAdd = AppliesRevertsAndRedoes(chart, add);
        add.Revert(chart);
        var afterRemove = AppliesRevertsAndRedoes(chart, remove);

        Assert.AreSequenceEqual([TextEvent.Of(0, "[idle]"), TextEvent.Of(240, "[play]")], afterAdd.PartOf(Guitar)!.TextEvents.ToList());
        Assert.IsEmpty(afterRemove.PartOf(Guitar)!.TextEvents);
        Assert.AreSequenceEqual([new ChartLocation(Guitar, null, 240, 240)], add.Affected.Regions);
        Assert.AreEqual(SongChanges.None, add.Affected.Song);
    }

    [TestMethod]
    public void RemoveTextEventRefusesAnEventTheTrackHasNot()
    {
        var chart = EditableChartSamples.Full();

        Refuses<InvalidOperationException>(chart, new RemoveTextEvent(null, TextEvent.Of(960, "[section Outro]")));
        Refuses<InvalidOperationException>(chart, new RemoveTextEvent(ChartInstrument.Bass, TextEvent.Of(0, "[idle]")));
    }
}

/// <summary>Checks a command applies, reverts and redoes exactly, or refuses without changing anything.</summary>
internal static class CommandChecks
{
    /// <summary>
    /// Applies <paramref name="command"/> to <paramref name="chart"/>, checks reverting gives back the chart it started as
    /// and applying again the chart the first apply gave, and leaves it applied.
    /// </summary>
    /// <returns>A copy of the chart as the command left it.</returns>
    public static EditableChart AppliesRevertsAndRedoes(EditableChart chart, IChartCommand command)
    {
        var before = chart.Clone();
        command.Apply(chart);
        var after = chart.Clone();
        Assert.IsFalse(command.Affected.IsEmpty, "The command says it touched nothing.");

        command.Revert(chart);
        AssertSame(before, chart, "after reverting");
        command.Apply(chart);
        AssertSame(after, chart, "after applying again");
        return after;
    }

    /// <summary>Checks applying <paramref name="command"/> throws <typeparamref name="T"/> and changes nothing.</summary>
    public static void Refuses<T>(EditableChart chart, IChartCommand command)
        where T : Exception
    {
        var before = chart.Clone();
        var e = Assert.Throws<T>(() => command.Apply(chart));
        Assert.IsFalse(string.IsNullOrWhiteSpace(e.Message));
        AssertSame(before, chart, "after refusing");
        Assert.IsTrue(command.Affected.IsEmpty);
    }

    /// <summary>Checks the charts hold the same.</summary>
    public static void AssertSame(EditableChart expected, EditableChart actual, string when)
    {
        var differences = ChartModelComparer.Differences(expected, actual);
        Assert.IsEmpty(differences, $"{when}: {DescribeModel(differences)}");
    }
}
