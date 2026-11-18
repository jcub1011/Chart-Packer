using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Sync tool on a vocal part (issue 272): a region drawn in the part's rows, whose edits to its notes and lyrics are
/// made again in every passage of the part holding exactly the same notes, by offset, length and pitch, and the same
/// lyrics, as one undoable step. Phrases and star power are neither matched nor copied.
/// </summary>
[TestClass]
public sealed class VocalSyncTests
{
    private const long Measure = 1920;

    // A measure's line, by offset from its start: three sung notes, a beat apart, each with its syllable.
    private static readonly VocalNoteEvent[] Line = [new(0, 240, 60), new(480, 240, 62), new(960, 480, 64)];

    private static readonly VocalLyricEvent[] Words = [new(0, "hel-"), new(480, "lo"), new(960, "world")];

    /// <summary>A session whose vocal part has <see cref="Line"/>, with <see cref="Words"/>, in each of <paramref name="measures"/>.</summary>
    private static EditorSession WithLines(params int[] measures) => Of(WithVocals(
        [.. measures.SelectMany(m => Line.Select(n => n with { Tick = (m * Measure) + n.Tick }))],
        [.. measures.SelectMany(m => Words.Select(l => l with { Tick = (m * Measure) + l.Tick }))]));

    /// <summary>The part's notes from <paramref name="start"/> up to a measure on, by offset.</summary>
    private static List<VocalNoteEvent> NotesOfMeasureAt(EditorSession session, long start) =>
        [.. Vocals(session).Notes.Where(n => n.Tick >= start && n.Tick < start + Measure).Select(n => n with { Tick = n.Tick - start })];

    /// <summary>The part's lyrics from <paramref name="start"/> up to a measure on, by offset.</summary>
    private static List<VocalLyricEvent> LyricsOfMeasureAt(EditorSession session, long start) =>
        [.. Vocals(session).Lyrics.Where(l => l.Tick >= start && l.Tick < start + Measure).Select(l => l with { Tick = l.Tick - start })];

    private static void SyncFirstMeasure(EditorSession session) =>
        Assert.IsTrue(session.Sync.Define(VocalTrack.Vocals, 0, Measure), session.Status);

    private const EditorModifiers CtrlShift = EditorModifiers.Control | EditorModifiers.Shift;

    [TestMethod]
    public void DrawingARegionOnAVocalPartFindsThePassagesWithTheSameNotesAndLyrics()
    {
        var notes = new List<VocalNoteEvent>();
        var lyrics = new List<VocalLyricEvent>();
        void Lay(int measure, VocalNoteEvent[] laid, VocalLyricEvent[] sung)
        {
            notes.AddRange(laid.Select(n => n with { Tick = (measure * Measure) + n.Tick }));
            lyrics.AddRange(sung.Select(l => l with { Tick = (measure * Measure) + l.Tick }));
        }

        Lay(0, Line, Words);
        Lay(2, Line, Words);
        Lay(4, Line, [Words[0], Words[1], new(960, "there")]);
        Lay(6, [Line[0], Line[1], new(960, 480, 65)], Words);
        Lay(8, [Line[0], new(480, 120, 62), Line[2]], Words);
        Lay(10, [.. Line, new(1440, 120, 60)], Words);
        Lay(12, [.. Line.Select(n => n with { Velocity = 64 })], Words);
        Lay(14, Line, Words);
        var session = Of(WithVocals([.. notes], [.. lyrics], [new(14 * Measure, Measure)], starPower: [new(14 * Measure, 480)]));

        SyncFirstMeasure(session);

        Assert.IsTrue(session.Sync.IsActive);
        Assert.AreEqual(VocalTrack.Vocals, session.Sync.Vocal);
        Assert.IsNull(session.Sync.Track);
        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreSequenceEqual([2 * Measure, 12 * Measure, 14 * Measure], session.Sync.Matches.ToList(),
            "A lyric, a pitch, a length or a note more apart doesn't match; a velocity, a phrase or star power apart does.");
    }

    [TestMethod]
    public void ARegionOverNoNotesIsKeptUnmatched()
    {
        var session = WithLines(2, 4);

        SyncFirstMeasure(session);

        Assert.IsTrue(session.Sync.IsUnmatched);
        Assert.IsEmpty(session.Sync.Matches);
    }

    [TestMethod]
    public void AnotherVocalPartIsntSearched()
    {
        var chart = WithVocals([.. Line], [.. Words]);
        WithVocals([.. Line.Select(n => n with { Tick = (2 * Measure) + n.Tick })], [.. Words.Select(l => l with { Tick = (2 * Measure) + l.Tick })],
            track: VocalTrack.Harmony1, chart: chart);
        var session = Of(chart);

        SyncFirstMeasure(session);

        Assert.IsTrue(session.Sync.IsUnmatched);
    }

    [TestMethod]
    public void ChangingANotesPitchInTheRegionChangesItInEveryPassageAsOneStep()
    {
        var session = WithLines(0, 2, 4);
        SyncFirstMeasure(session);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(480, 62)]);

        session.Tools.SetVocalPitch(67);

        VocalNoteEvent[] changed = [Line[0], Line[1] with { Pitch = 67 }, Line[2]];
        foreach (long start in new[] { 0, 2 * Measure, 4 * Measure })
        {
            Assert.AreSequenceEqual(changed, NotesOfMeasureAt(session, start));
            Assert.AreSequenceEqual(Words, LyricsOfMeasureAt(session, start), "The lyric stays on its note.");
        }

        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.AreSequenceEqual([2 * Measure, 4 * Measure], session.Sync.Matches.ToList(), "The passages still match.");

        session.UndoStack.Undo();
        foreach (long start in new[] { 0, 2 * Measure, 4 * Measure })
            Assert.AreSequenceEqual(Line, NotesOfMeasureAt(session, start));

        session.UndoStack.Redo();
        foreach (long start in new[] { 0, 2 * Measure, 4 * Measure })
            Assert.AreSequenceEqual(changed, NotesOfMeasureAt(session, start), "Redo makes the copies again.");
    }

    [TestMethod]
    public void SettingALyricInTheRegionSetsItInEveryPassage()
    {
        var session = WithLines(0, 2);
        SyncFirstMeasure(session);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(960, 64)]);

        session.Tools.SetVocalLyric("there");

        VocalLyricEvent[] changed = [Words[0], Words[1], new(960, "there")];
        Assert.AreSequenceEqual(changed, LyricsOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(changed, LyricsOfMeasureAt(session, 2 * Measure));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void MovingANoteInTheRegionMovesItWithItsLyricInEveryPassage()
    {
        var session = WithLines(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Select;

        DragFromTo(session, VocalAt(970, 64), VocalAt(1210, 64));

        VocalNoteEvent[] moved = [Line[0], Line[1], Line[2] with { Tick = 1200 }];
        VocalLyricEvent[] carried = [Words[0], Words[1], Words[2] with { Tick = 1200 }];
        Assert.AreSequenceEqual(moved, NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(moved, NotesOfMeasureAt(session, 2 * Measure));
        Assert.AreSequenceEqual(carried, LyricsOfMeasureAt(session, 2 * Measure));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void DeletingANoteInTheRegionDeletesItWithItsLyricFromEveryPassage()
    {
        var session = WithLines(0, 2);
        SyncFirstMeasure(session);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(480, 62)]);
        session.Tool = EditorTool.Select;

        session.Tools.DeleteSelection();

        VocalNoteEvent[] left = [Line[0], Line[2]];
        Assert.AreSequenceEqual(left, NotesOfMeasureAt(session, 0));
        Assert.AreSequenceEqual(left, NotesOfMeasureAt(session, 2 * Measure), "The note goes from the passage too, with its lyric.");
        Assert.AreSequenceEqual([Words[0], Words[2]], LyricsOfMeasureAt(session, 2 * Measure));
    }

    [TestMethod]
    public void PhrasesAndStarPowerArentCopiedOrTouched()
    {
        var session = Of(WithVocals(
            [.. Line, .. Line.Select(n => n with { Tick = (2 * Measure) + n.Tick })],
            [.. Words, .. Words.Select(l => l with { Tick = (2 * Measure) + l.Tick })],
            [new(0, Measure)],
            starPower: [new(2 * Measure, 480)]));
        SyncFirstMeasure(session);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, [new(0, 60)]);

        session.Tools.SetVocalPitch(59);

        Assert.AreEqual(59, NotesOfMeasureAt(session, 2 * Measure)[0].Pitch);
        Assert.AreSequenceEqual([new VocalPhraseEvent(0, Measure)], Vocals(session).Phrases.ToList(), "The region's phrase isn't copied.");
        Assert.AreSequenceEqual([new VocalStarPowerEvent(2 * Measure, 480)], Vocals(session).StarPower.ToList(), "The passage's star power stays.");
    }

    [TestMethod]
    public void APassageEditedByHandStopsBeingSyncedUntilTheEditIsUndone()
    {
        var session = WithLines(0, 2, 4);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Tools.SetVocalLyric(VocalTrack.Vocals, (2 * Measure) + 960, "there"));

        Assert.AreSequenceEqual([4 * Measure], session.Sync.Matches.ToList());
        Assert.IsTrue(session.Tools.SetVocalLyric(VocalTrack.Vocals, 480, "la"));
        Assert.AreEqual("there", LyricsOfMeasureAt(session, 2 * Measure)[2].Text, "A passage that no longer matches isn't written over.");
        Assert.AreEqual("lo", LyricsOfMeasureAt(session, 2 * Measure)[1].Text);
        Assert.AreEqual("la", LyricsOfMeasureAt(session, 4 * Measure)[1].Text);

        session.UndoStack.Undo();
        session.UndoStack.Undo();
        Assert.AreSequenceEqual([2 * Measure, 4 * Measure], session.Sync.Matches.ToList());
    }

    [TestMethod]
    public void AnEditOutsideTheRegionIsntCopied()
    {
        var session = WithLines(0, 2);
        SyncFirstMeasure(session);

        Assert.IsTrue(session.Tools.SetVocalLyric(VocalTrack.Vocals, (2 * Measure) + 480, "la"));

        Assert.AreSequenceEqual(Words, LyricsOfMeasureAt(session, 0));
        Assert.IsEmpty(session.Sync.Matches);
    }

    [TestMethod]
    public void TheRegionGoesWithItsVocalPart()
    {
        var session = Of(Guitar());
        Assert.IsTrue(session.Execute(new AddVocalPart(VocalTrack.Vocals)));
        Assert.IsTrue(session.Execute(new ChangeVocals(VocalTrack.Vocals, Vocals(session).With(notes: Line, lyrics: Words))));
        SyncFirstMeasure(session);

        session.UndoStack.Undo();
        Assert.IsTrue(session.Sync.IsActive, "A region over no notes is kept.");
        session.UndoStack.Undo();

        Assert.IsFalse(session.Sync.IsActive);
    }

    [TestMethod]
    public void DraggingWithTheSyncToolInTheRowsDrawsARegionAndGoesBackToTheToolBefore()
    {
        var session = WithLines(0, 2);
        session.Tool = EditorTool.Place;
        session.Tool = EditorTool.Sync;

        DragFromTo(session, VocalAt(10, 60), VocalAt(1910, 55));

        Assert.AreEqual(VocalTrack.Vocals, session.Sync.Vocal);
        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreSequenceEqual([2 * Measure], session.Sync.Matches.ToList());
        Assert.AreEqual(EditorTool.Place, session.Tool);
        Assert.IsNull(session.Sync.Draft);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.HasCount(6, Vocals(session).Notes, "Nothing's placed.");
    }

    [TestMethod]
    public void TheDraftIsOnTheVocalPartsLane()
    {
        var session = WithLines(0, 2);
        session.Tool = EditorTool.Sync;

        session.Tools.Press(VocalAt(10, 60));
        session.Tools.Drag(VocalAt(1910, 60));

        Assert.AreEqual((LanePart.Vocals(VocalTrack.Vocals), 0L, Measure), session.Sync.Draft);
        session.Tools.Release(VocalAt(1910, 60));
        Assert.IsNull(session.Sync.Draft);
    }

    [TestMethod]
    public void HoldingCtrlAndShiftDrawsWithAnyToolRatherThanSelecting()
    {
        var session = WithLines(0, 2);
        session.Tool = EditorTool.Select;

        DragFromTo(session, VocalAt(10, 60), VocalAt(1910, 60), CtrlShift);

        Assert.AreEqual((0L, Measure), session.Sync.Span);
        Assert.AreEqual(EditorTool.Select, session.Tool);
        Assert.IsEmpty(session.Selection.VocalPlaces);
        Assert.AreSequenceEqual(Line, NotesOfMeasureAt(session, 0), "Nothing moves.");
    }

    [TestMethod]
    public void TheSyncToolDoesntDrawInThePhraseStripTheStarPowerRailOrPastTheRows()
    {
        var session = WithLines(0, 2);
        session.Tool = EditorTool.Sync;

        DragFromTo(session, PhraseStripAt(10), PhraseStripAt(1910));
        DragFromTo(session, StarPowerRailAt(10), StarPowerRailAt(1910));
        DragFromTo(session, VocalAt(10, null), VocalAt(1910, null));

        Assert.IsFalse(session.Sync.IsActive);
        Assert.IsEmpty(Vocals(session).Phrases);
        Assert.IsEmpty(Vocals(session).StarPower);
    }

    [TestMethod]
    public void HoldingCtrlAndShiftAlsoResizesMovesAndMakesANewSource()
    {
        var session = WithLines(0, 2, 4);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Place;

        Click(session, VocalAt((2 * Measure) + 480, 62), CtrlShift);
        Assert.AreEqual((2 * Measure, 3 * Measure), session.Sync.Span, "A click on a synced passage makes it the source.");

        DragFromTo(session, VocalAt(3 * Measure, 60), VocalAt(30, 60), CtrlShift);
        Assert.AreEqual((2 * Measure, (2 * Measure) + 60), session.Sync.Span, "A drag of the region's end resizes it, never shorter than a 32nd note.");

        DragFromTo(session, VocalAt((2 * Measure) + 30, 60), VocalAt((2 * Measure) + 30 - Measure, 60), CtrlShift);
        Assert.AreEqual((Measure, Measure + 60), session.Sync.Span, "A drag of the region's body moves it.");
        Assert.AreEqual(EditorTool.Place, session.Tool);
        Assert.HasCount(9, Vocals(session).Notes, "Nothing's placed.");
    }

    [TestMethod]
    public void TheSyncLineIsWhereANewRegionWouldStartInTheRows()
    {
        var session = WithLines(0, 2, 4);
        session.Tool = EditorTool.Sync;

        Assert.AreEqual(120L, session.Tools.SyncDrawStart(VocalAt(130, 60)), "The grid step the press would snap to.");
        Assert.IsNull(session.Tools.SyncDrawStart(VocalAt(130, 60), EditorModifiers.Shift), "Shift alone lends any tool the select tool's press.");
        Assert.IsNull(session.Tools.SyncDrawStart(PhraseStripAt(130)), "Only in the rows.");
        Assert.IsNull(session.Tools.SyncDrawStart(StarPowerRailAt(130)));
        Assert.IsNull(session.Tools.SyncDrawStart(VocalAt(130, null)));

        SyncFirstMeasure(session);
        Assert.IsNull(session.Tools.SyncDrawStart(VocalAt(960, 60)), "On the region's body, a drag moves it.");
        Assert.IsNull(session.Tools.SyncDrawStart(VocalAt((2 * Measure) + 480, 60)), "On a passage synced with it, a click makes it the source.");
        Assert.AreEqual(3 * Measure, session.Tools.SyncDrawStart(VocalAt((3 * Measure) + 10, 60)), "Elsewhere, a drag draws a new region.");
        Assert.AreEqual(SyncGrip.Body, session.Tools.SyncHitOf(VocalAt(960, 60))?.Grip);
        Assert.IsNull(session.Tools.SyncHitOf(VocalAt(960, 60, VocalTrack.Harmony1)), "Another part's lane doesn't take hold of it.");
    }

    [TestMethod]
    public void TheMiddleButtonTakesTheRegionAwayRatherThanErasingNotes()
    {
        var session = WithLines(0, 2);
        SyncFirstMeasure(session);
        session.Tool = EditorTool.Sync;

        session.Tools.PressErase(VocalAt(480, 62));

        Assert.IsFalse(session.Sync.IsActive);
        Assert.AreSequenceEqual(Line, NotesOfMeasureAt(session, 0));
    }

    [TestMethod]
    public void FindingGoesThroughThePassagesMatchingTheSelectedVocalNotes()
    {
        var session = WithLines(0, 2, 4);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, Line.Select(static n => n.Place));

        session.Find.Start();

        Assert.AreEqual(VocalTrack.Vocals, session.Find.Vocal);
        Assert.IsNull(session.Find.Track);
        Assert.AreSequenceEqual([0L, 2 * Measure, 4 * Measure], session.Find.Matches.ToList(), "The notes searched for are a match too");
        Assert.AreEqual(2 * Measure, session.CursorTick);

        session.Find.Start();
        Assert.AreEqual(4 * Measure, session.CursorTick, "Again goes on to the next.");
    }

    [TestMethod]
    public void FindingSaysSoWhenNoOtherPassageMatchesTheSelectedVocalNotes()
    {
        var session = WithLines(0);
        session.Selection.SelectVocalNotes(VocalTrack.Vocals, Line.Select(static n => n.Place));

        session.Find.Start();

        Assert.IsFalse(session.Find.HasMatches);
        Assert.IsNotNull(session.Status);
    }
}
