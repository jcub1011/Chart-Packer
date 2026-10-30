using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Metadata;

[TestClass]
public sealed class DraftHistoryTests
{
    // A clock the test moves by hand.
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [TestMethod]
    public void AFreshHistoryHasNothingToUndoOrRedo()
    {
        var history = new DraftHistory(new MetadataDraft());

        Assert.IsFalse(history.CanUndo);
        Assert.IsFalse(history.CanRedo);
        Assert.ThrowsExactly<InvalidOperationException>(() => history.Undo());
        Assert.ThrowsExactly<InvalidOperationException>(() => history.Redo());
    }

    [TestMethod]
    public void UndoGoesBackToTheBaselineGivenNotTheDraftAsItWas()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "A");
        var history = new DraftHistory(draft, baseline: new MetadataDraft());

        Assert.IsFalse(history.CanUndo);
        Assert.IsTrue(history.Record());
        Assert.IsTrue(history.CanUndo);

        history.Undo();

        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void UndoAndRedoWalkTheChangesInOrder()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);

        draft.Set("name", "A");
        history.Record();
        draft.Set("name", "B");
        history.Record();
        draft.SetAlbumArt("a.png");
        history.Record();

        history.Undo();
        Assert.IsNull(draft.AlbumArt);
        Assert.AreEqual("B", Value(draft, "name"));

        history.Undo();
        Assert.AreEqual("A", Value(draft, "name"));

        history.Undo();
        Assert.IsTrue(draft.IsEmpty);
        Assert.IsFalse(history.CanUndo);
        Assert.AreEqual(3, history.RedoCount);

        history.Redo();
        history.Redo();
        history.Redo();
        Assert.AreEqual("B", Value(draft, "name"));
        Assert.AreEqual(new AlbumArtChange("a.png"), draft.AlbumArt);
        Assert.IsFalse(history.CanRedo);
    }

    [TestMethod]
    public void UndoRestoresTheSameDraftObject()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);
        draft.Set("name", "A");
        history.Record();

        history.Undo();

        Assert.IsTrue(draft.IsEmpty);
        draft.Set("name", "C");
        Assert.IsTrue(history.Record());
        Assert.AreEqual(1, history.UndoCount);
    }

    [TestMethod]
    public void ANewChangeDropsTheStepsUndone()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);
        draft.Set("name", "A");
        history.Record();
        draft.Set("name", "B");
        history.Record();
        history.Undo();

        draft.Set("name", "C");
        history.Record();

        Assert.IsFalse(history.CanRedo);
        Assert.AreEqual(2, history.UndoCount);
        history.Undo();
        Assert.AreEqual("A", Value(draft, "name"));
    }

    [TestMethod]
    public void RecordingAnUnchangedDraftIsNotAStep()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);

        Assert.IsFalse(history.Record());

        draft.Set("name", "A");
        Assert.IsTrue(history.Record());
        Assert.IsFalse(history.Record());
        draft.Set("name", "A");
        Assert.IsFalse(history.Record());
        Assert.AreEqual(1, history.UndoCount);
    }

    [TestMethod]
    public void ChangesWithTheSameKeyInTheWindowAreOneStep()
    {
        var clock = new FakeClock();
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft, clock);
        draft.Set("name", "A");
        history.Record();

        for (int offset = 10; offset <= 50; offset += 10)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            draft.SetVocalsOffset(offset);
            history.Record("offsets");
        }

        // The first drag step starts the step; the rest, within a second of each other, merge into it.
        Assert.AreEqual(2, history.UndoCount);
        history.Undo();
        Assert.AreEqual(0, draft.VocalsOffset);
        Assert.AreEqual("A", Value(draft, "name"));
        history.Redo();
        Assert.AreEqual(50, draft.VocalsOffset);
    }

    [TestMethod]
    public void ChangesAfterTheWindowOrWithAnotherKeyAreSeparateSteps()
    {
        var clock = new FakeClock();
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft, clock);

        draft.SetVocalsOffset(10);
        history.Record("offsets");
        clock.Advance(DraftHistory.DefaultMergeWindow + TimeSpan.FromMilliseconds(1));
        draft.SetVocalsOffset(20);
        history.Record("offsets");
        draft.Set("delay", "5");
        history.Record("timing");
        draft.Set("delay", "6");
        history.Record(null);
        draft.Set("delay", "7");
        history.Record(null);

        Assert.AreEqual(5, history.UndoCount);
    }

    [TestMethod]
    public void ADragBackToWhereItStartedLeavesNoStep()
    {
        var clock = new FakeClock();
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft, clock);
        draft.Set("name", "A");
        history.Record();

        draft.SetVocalsOffset(10);
        history.Record("offsets");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        draft.SetVocalsOffset(0);
        history.Record("offsets");

        Assert.AreEqual(1, history.UndoCount);
        history.Undo();
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void UndoingEndsTheRunBeingMerged()
    {
        var clock = new FakeClock();
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft, clock);
        draft.SetVocalsOffset(10);
        history.Record("offsets");
        history.Undo();

        draft.SetVocalsOffset(20);
        history.Record("offsets");

        Assert.AreEqual(1, history.UndoCount);
        history.Undo();
        Assert.AreEqual(0, draft.VocalsOffset);
    }

    [TestMethod]
    public void TheOldestStepsAreDroppedBeyondTheCapacity()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft, capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            draft.Set("name", i.ToString());
            history.Record();
        }

        Assert.AreEqual(3, history.UndoCount);
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.IsFalse(history.CanUndo);
        Assert.AreEqual("2", Value(draft, "name"));
    }

    [TestMethod]
    public void ResetMakesTheDraftAsItIsTheStateToUndoTo()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);
        draft.Set("name", "A");
        history.Record();
        history.Undo();

        draft.Clear();
        history.Reset();

        Assert.IsFalse(history.CanUndo);
        Assert.IsFalse(history.CanRedo);
        draft.Set("name", "B");
        history.Record();
        history.Undo();
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void UndoSaysWhetherOnlyKeysChanged()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);
        draft.Set("name", "A");
        history.Record();
        draft.SetAlbumArt("a.png");
        history.Record();

        Assert.IsFalse(history.Undo());
        Assert.IsTrue(history.Undo());
        Assert.IsTrue(history.Redo());
        Assert.IsFalse(history.Redo());
    }

    [TestMethod]
    public void UndoRestoresAFileAddWithTheRemovalsAndKeysItMade()
    {
        var draft = new MetadataDraft();
        var history = new DraftHistory(draft);
        draft.AddFile(new AddedFile("notes.mid", "n.mid", 1), ["notes.chart"], [new MetadataChange("delay", "500")]);
        history.Record();

        history.Undo();
        Assert.IsTrue(draft.IsEmpty);

        history.Redo();

        // The add's own bookkeeping came back too: reverting it still undoes the removal and the key.
        draft.RevertFile("notes.mid");
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void EachSongsHistoryIsItsOwn()
    {
        var first = new MetadataDraft();
        var second = new MetadataDraft();
        var firstHistory = new DraftHistory(first);
        var secondHistory = new DraftHistory(second);

        // A batch edit: the same change to each song, recorded in each song's history.
        foreach (var (draft, history) in new[] { (first, firstHistory), (second, secondHistory) })
        {
            draft.Set("genre", "Rock");
            history.Record();
        }

        // An edit to the second song alone, after the batch.
        second.Set("name", "Two");
        secondHistory.Record();

        // Undoing the batch for the first song leaves the second as it is, and the other way round.
        firstHistory.Undo();
        Assert.IsTrue(first.IsEmpty);
        Assert.AreEqual("Rock", Value(second, "genre"));
        Assert.AreEqual("Two", Value(second, "name"));
        Assert.AreEqual(2, secondHistory.UndoCount);

        secondHistory.Undo();
        secondHistory.Undo();
        Assert.IsTrue(second.IsEmpty);
        Assert.IsTrue(first.IsEmpty);

        firstHistory.Redo();
        Assert.AreEqual("Rock", Value(first, "genre"));
        Assert.IsTrue(second.IsEmpty);
    }

    private static string? Value(MetadataDraft draft, string key) =>
        draft.TryGetChange(key, out var change) ? change.Value : throw new AssertFailedException($"{key} has no change.");
}
