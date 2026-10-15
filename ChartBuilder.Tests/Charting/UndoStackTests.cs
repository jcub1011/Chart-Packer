using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using static ChartBuilder.Tests.Charting.CommandChecks;

namespace ChartBuilder.Tests.Charting;

/// <summary>Composite commands, the undo stack and what a change touched.</summary>
[TestClass]
public sealed class UndoStackTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    // Composite commands.

    [TestMethod]
    public void ACompositeAppliesItsCommandsInOrderAndRevertsThemInReverse()
    {
        var chart = EditableChartSamples.Full();
        var command = new CompositeCommand(
        [
            new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]),
            new SetChordFlags(Guitar, Expert, [1920], ChordFlags.Tap, on: true),
            new MoveNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)], 480, 1),
        ]);

        var after = AppliesRevertsAndRedoes(chart, command);

        var track = after.PartOf(Guitar)![Expert];
        Assert.IsTrue(track.TryGetNote(2400, 2, false, out _));
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(2400));
        Assert.AreSequenceEqual([new ChartLocation(Guitar, Expert, 1920, 1921), new ChartLocation(Guitar, Expert, 2400, 2401)], command.Affected.Regions);
    }

    [TestMethod]
    public void ACompositeWhoseCommandFailsRevertsTheOnesBeforeItAndChangesNothing()
    {
        var chart = EditableChartSamples.Full();
        var first = new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]);
        var command = new CompositeCommand(
        [
            first,
            new AddTempo(new TempoChange(960, 400_000)),
            new AddNotes(Guitar, Expert, [new EditableNote(1920, 5, 1)]),
        ]);

        Refuses<InvalidOperationException>(chart, command);

        // The first command was reverted, so it can be applied again.
        first.Apply(chart);
    }

    [TestMethod]
    public void AnEmptyCompositeChangesNothing()
    {
        var chart = EditableChartSamples.Full();
        var before = chart.Clone();
        var command = new CompositeCommand([]);

        command.Apply(chart);

        Assert.IsTrue(command.Affected.IsEmpty);
        AssertSame(before, chart, "after applying");
    }

    // The undo stack.

    [TestMethod]
    public void UndoAndRedoRunInOrder()
    {
        var chart = EditableChartSamples.Full();
        var stack = new ChartUndoStack(chart);
        var states = new List<EditableChart> { chart.Clone() };
        IChartCommand[] commands =
        [
            new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]),
            new MoveNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)], 480, 0),
            new SetTempo(new TempoChange(1920, 400_000), 300_000),
        ];

        foreach (var command in commands)
        {
            stack.Execute(command);
            states.Add(chart.Clone());
        }

        Assert.AreEqual((3, 0), (stack.UndoCount, stack.RedoCount));
        Assert.AreSame(commands[2], stack.NextUndo);
        for (int i = commands.Length - 1; i >= 0; i--)
        {
            stack.Undo();
            AssertSame(states[i], chart, $"after undoing command {i}");
        }

        Assert.IsFalse(stack.CanUndo);
        Assert.AreSame(commands[0], stack.NextRedo);
        for (int i = 0; i < commands.Length; i++)
        {
            stack.Redo();
            AssertSame(states[i + 1], chart, $"after redoing command {i}");
        }

        Assert.IsFalse(stack.CanRedo);
        Assert.ThrowsExactly<InvalidOperationException>(stack.Redo);
    }

    [TestMethod]
    public void ExecutingDropsTheRedoSteps()
    {
        var stack = new ChartUndoStack(EditableChartSamples.Full());
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]));
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(2400, 1, 1)]));
        stack.Undo();
        Assert.IsTrue(stack.CanRedo);

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(2880, 1, 1)]));

        Assert.IsFalse(stack.CanRedo);
        Assert.AreEqual(2, stack.UndoCount);
        Assert.IsFalse(stack.Chart.PartOf(Guitar)![Expert].TryGetNote(2400, 1, false, out _));
    }

    [TestMethod]
    public void ACommandThatFailsLeavesTheStackAndTheChartAsTheyWere()
    {
        var chart = EditableChartSamples.Full();
        var stack = new ChartUndoStack(chart);
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]));
        stack.Undo();
        var before = chart.Clone();
        int raised = 0;
        stack.Changed += (_, _) => raised++;

        Assert.ThrowsExactly<InvalidOperationException>(() => stack.Execute(new RemoveTempo(new TempoChange(0, 500_000))));

        AssertSame(before, chart, "after the failed command");
        Assert.AreEqual((0, 1, 0), (stack.UndoCount, stack.RedoCount, raised));
        Assert.IsFalse(stack.IsDirty);
    }

    [TestMethod]
    public void UndoingWithNothingToUndoThrows()
    {
        var stack = new ChartUndoStack(EditableChartSamples.Full());

        Assert.ThrowsExactly<InvalidOperationException>(stack.Undo);
        Assert.IsNull(stack.NextUndo);
    }

    [TestMethod]
    public void ChangedIsRaisedAfterEachDoUndoAndRedoWithTheCommand()
    {
        var chart = EditableChartSamples.Full();
        var stack = new ChartUndoStack(chart);
        var command = new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]);
        var raised = new List<(IChartCommand Command, ChartCommandAction Action, bool HasNote, ChartChange Affected)>();
        stack.Changed += (sender, e) =>
        {
            Assert.AreSame(stack, sender);
            raised.Add((e.Command, e.Action, chart.PartOf(Guitar)![Expert].TryGetNote(1920, 1, false, out _), e.Affected));
        };

        stack.Execute(command);
        stack.Undo();
        stack.Redo();

        Assert.AreSequenceEqual(
            [(command, ChartCommandAction.Do, true), (command, ChartCommandAction.Undo, false), (command, ChartCommandAction.Redo, true)],
            raised.Select(r => (r.Command, r.Action, r.HasNote)));
        Assert.IsTrue(raised.All(r => r.Affected.Regions.SequenceEqual([new ChartLocation(Guitar, Expert, 1920, 1921)])));
    }

    [TestMethod]
    public void IsDirtyFollowsTheSavePointThroughUndoAndRedo()
    {
        var stack = new ChartUndoStack(EditableChartSamples.Full());
        Assert.IsFalse(stack.IsDirty);

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]));
        Assert.IsTrue(stack.IsDirty);
        stack.Undo();
        Assert.IsFalse(stack.IsDirty);
        stack.Redo();
        Assert.IsTrue(stack.IsDirty);

        stack.MarkSaved();
        Assert.IsFalse(stack.IsDirty);
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(2400, 1, 1)]));
        Assert.IsTrue(stack.IsDirty);
        stack.Undo();
        Assert.IsFalse(stack.IsDirty);
        stack.Undo();
        Assert.IsTrue(stack.IsDirty);
        stack.Redo();
        Assert.IsFalse(stack.IsDirty);
    }

    [TestMethod]
    public void TheChartIsDirtyForGoodOnceTheSavedStateIsDroppedFromTheRedoSteps()
    {
        var stack = new ChartUndoStack(EditableChartSamples.Full());
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 1)]));
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(2400, 1, 1)]));
        stack.MarkSaved();
        stack.Undo();
        stack.Undo();

        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(2880, 1, 1)]));
        Assert.IsTrue(stack.IsDirty);
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(3360, 1, 1)]));
        Assert.IsTrue(stack.IsDirty);
        stack.Undo();
        stack.Undo();
        Assert.IsTrue(stack.IsDirty);

        stack.MarkSaved();
        Assert.IsFalse(stack.IsDirty);
    }

    // What a change touched.

    [TestMethod]
    public void MergingJoinsRegionsOfOneTrackThatMeetOrTouch()
    {
        var a = ChartChange.Of([new ChartLocation(Guitar, Expert, 0, 10), new ChartLocation(Guitar, null, 5, 6)]);
        var b = ChartChange.Of([new ChartLocation(Guitar, Expert, 11, 20), new ChartLocation(Guitar, Expert, 40, 50), new ChartLocation(ChartInstrument.Bass, Expert, 0, 1)], SongChanges.Events);

        var merged = a.Merge(b);

        Assert.AreSequenceEqual(
            [new ChartLocation(Guitar, null, 5, 6), new ChartLocation(Guitar, Expert, 0, 20), new ChartLocation(Guitar, Expert, 40, 50), new ChartLocation(ChartInstrument.Bass, Expert, 0, 1)],
            merged.Regions);
        Assert.AreEqual(SongChanges.Events, merged.Song);
        Assert.AreSame(a, a.Merge(ChartChange.None));
        Assert.IsTrue(ChartChange.Union([]).IsEmpty);
        Assert.IsTrue(ChartChange.Of(SongChanges.TempoMap).TempoMapChanged);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChartChange.Of(Guitar, Expert, 10, 9));
    }
}
