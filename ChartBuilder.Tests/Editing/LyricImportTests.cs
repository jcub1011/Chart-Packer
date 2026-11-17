using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Importing lyrics read from a file into a vocal part (issue 270): every word a spoken note, one phrase to a line, the
/// whole import one undoable step. At 480 ticks a beat and 120 BPM a second is 960 ticks.
/// </summary>
[TestClass]
public sealed class LyricImportTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static LyricDocument TwoLines() => new(
    [
        // "ab" and "cde" share the second by their letters: 2 to 3.
        LyricLine.OfText(S(1), S(2), "ab cde"),
        new LyricLine(S(3), S(4), [new LyricWord("one", S(3), S(3.5)), new LyricWord("two", S(3.5))]),
    ]);

    [TestMethod]
    public void EveryWordIsASpokenNoteAndEachLineAPhrase()
    {
        var session = Of(WithVocals([]));

        var result = LyricImport.Apply(session, VocalTrack.Vocals, TwoLines(), LyricImportMode.Replace);

        Assert.IsTrue(result.Done);
        Assert.AreEqual(4, result.Words);
        var content = Vocals(session);
        Assert.AreSequenceEqual([960L, 1344L, 2880L, 3360L], [.. content.Notes.Select(static n => n.Tick)]);
        Assert.IsTrue(content.Notes.All(static n => n.Pitch == VocalLane.SpokenPitch));
        Assert.AreSequenceEqual(["ab#", "cde#", "one#", "two#"], [.. content.Lyrics.Select(static l => l.Text)]);
        Assert.IsTrue(content.Notes.All(n => VocalEdits.IsSpoken(content, n.Tick)));
        Assert.AreSequenceEqual([new VocalPhraseEvent(960, 960), new VocalPhraseEvent(2880, 960)], [.. content.Phrases]);

        // Each note is held to the next word, or the line's end.
        Assert.AreSequenceEqual([384L, 576L, 480L, 480L], [.. content.Notes.Select(static n => n.Length)]);
    }

    [TestMethod]
    public void TheWholeImportIsOneUndoStep()
    {
        var chart = WithVocals([new(0, 100, 60)], [new(0, "old")]);
        var session = Of(chart);
        var before = Vocals(session);

        LyricImport.Apply(session, VocalTrack.Vocals, TwoLines(), LyricImportMode.Replace);

        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSame(before, Vocals(session));
    }

    [TestMethod]
    public void ReplaceTakesOutWhatThePartHadButItsStarPower()
    {
        var session = Of(WithVocals([new(0, 100, 60)], [new(0, "old")], [new(0, 200)], starPower: [new(0, 400)]));

        LyricImport.Apply(session, VocalTrack.Vocals, TwoLines(), LyricImportMode.Replace);

        var content = Vocals(session);
        Assert.IsFalse(content.Lyrics.Any(static l => l.Text == "old"));
        Assert.AreEqual(4, content.Notes.Count);
        Assert.AreEqual(1, content.StarPower.Count);
    }

    [TestMethod]
    public void MergeKeepsWhatThePartHasAndSkipsWordsOnItsNotes()
    {
        // The part's note at 1344 is where the second word falls, and its phrase covers the first line.
        var session = Of(WithVocals([new(1344, 100, 64)], [new(1344, "keep")], [new(1300, 300)]));

        var result = LyricImport.Apply(session, VocalTrack.Vocals, TwoLines(), LyricImportMode.Merge);

        Assert.AreEqual(3, result.Words);
        Assert.AreEqual(1, result.Skipped);
        var content = Vocals(session);
        Assert.AreEqual("keep", VocalEdits.LyricAt(content, 1344));
        Assert.AreEqual(new VocalNoteEvent(960, 384, 60), content.Notes[0]);
        Assert.AreEqual(5 - 1, content.Notes.Count);

        // The first line's phrase would overlap the part's own, so only the second is added.
        Assert.AreSequenceEqual([new VocalPhraseEvent(1300, 300), new VocalPhraseEvent(2880, 960)], [.. content.Phrases]);
    }

    [TestMethod]
    public void AMissingPartIsAddedInTheSameStep()
    {
        var session = Of(Guitar());

        var result = LyricImport.Apply(session, VocalTrack.Harmony1, TwoLines(), LyricImportMode.Replace);

        Assert.IsTrue(result.Done);
        Assert.AreEqual(4, Vocals(session, VocalTrack.Harmony1).Notes.Count);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.IsNull(session.VocalContentOf(VocalTrack.Harmony1));
    }

    [TestMethod]
    public void TheSongsDelayIsTakenOffTheFilesTimes()
    {
        var session = Of(WithVocals([]), delay: S(0.5));

        LyricImport.Apply(session, VocalTrack.Vocals, TwoLines(), LyricImportMode.Replace);

        // A word at 1 s in the audio is at 0.5 s in the chart.
        Assert.AreEqual(480, Vocals(session).Notes[0].Tick);
    }

    [TestMethod]
    public void AWordBeforeTheStartIsKeptAtItAndOneEndingBeforeItIsSkipped()
    {
        var session = Of(WithVocals([]));
        var document = new LyricDocument(
        [
            new LyricLine(S(-2), S(-1), [new LyricWord("gone", S(-2), S(-1))]),
            new LyricLine(S(-0.5), S(1), [new LyricWord("early", S(-0.5), S(1))]),
        ]);

        var result = LyricImport.Apply(session, VocalTrack.Vocals, document, LyricImportMode.Replace);

        Assert.AreEqual(1, result.Words);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(new VocalNoteEvent(0, 960, VocalLane.SpokenPitch), Vocals(session).Notes.Single());
    }

    [TestMethod]
    public void AWordOnAnotherWordsTickIsSkipped()
    {
        var session = Of(WithVocals([]));
        var document = new LyricDocument([new LyricLine(S(1), S(2), [new LyricWord("a", S(1)), new LyricWord("b", S(1)), new LyricWord("c", S(1.5))])]);

        var result = LyricImport.Apply(session, VocalTrack.Vocals, document, LyricImportMode.Replace);

        Assert.AreEqual(2, result.Words);
        Assert.AreEqual(1, result.Skipped);
    }

    [TestMethod]
    public void ChartSymbolsAtTheEndOfAWordAreTakenOffAndAJoinKept()
    {
        var session = Of(WithVocals([]));
        var document = new LyricDocument([new LyricLine(S(1), S(2), [new LyricWord("Hel-", S(1)), new LyricWord("lo^", S(1.5)), new LyricWord("+", S(1.8))])]);

        LyricImport.Apply(session, VocalTrack.Vocals, document, LyricImportMode.Replace);

        Assert.AreSequenceEqual(["Hel-#", "lo#"], [.. Vocals(session).Lyrics.Select(static l => l.Text)]);
    }

    [TestMethod]
    public void AnEmptyDocumentChangesNothing()
    {
        var session = Of(WithVocals([new(0, 100, 60)], [new(0, "old")]));

        var result = LyricImport.Apply(session, VocalTrack.Vocals, new LyricDocument([]), LyricImportMode.Replace);

        Assert.IsFalse(result.Done);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }
}
