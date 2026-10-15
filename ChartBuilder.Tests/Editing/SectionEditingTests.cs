using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Sections and the other text events in the editor (charter-spec C8 items 3 to 6, §9): Ctrl+↑ and Ctrl+↓, the Sections
/// tab's rows, and its inserting, renaming, moving and deleting, as the tools carry them out.
/// </summary>
[TestClass]
public sealed class SectionEditingTests
{
    // Ctrl+↑ and Ctrl+↓.

    [TestMethod]
    [DataRow(0L, 1, 960L)]
    [DataRow(0L, 2, 1920L)]
    [DataRow(1500L, 1, 1920L)]
    [DataRow(1500L, -1, 960L)]
    [DataRow(1920L, 1, 3840L)]
    [DataRow(1920L, -1, 960L)]
    [DataRow(5000L, -1, 3840L)]
    [DataRow(5000L, -2, 1920L)]
    public void TheCursorGoesToTheNextOrPreviousSectionsStart(long from, int sections, long to)
    {
        var session = Of(WithSections());
        session.MoveCursorTo(from);

        session.Tools.StepCursorSections(sections);

        Assert.AreEqual(to, CursorAt(session));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DataRow(3840L, 1, "after")]
    [DataRow(6000L, 1, "after")]
    [DataRow(960L, -1, "before")]
    [DataRow(0L, -1, "before")]
    [DataRow(1920L, 2, "after")]
    public void PastTheFirstOrLastSectionTheCursorStaysAndTheStatusSaysSo(long from, int sections, string way)
    {
        var session = Of(WithSections());
        session.MoveCursorTo(from);

        session.Tools.StepCursorSections(sections);

        Assert.AreEqual(from, CursorAt(session));
        Assert.Contains(way, session.Status!);
    }

    [TestMethod]
    public void OnlySectionsAreGoneToAndSectionsAtOneTickAreOneStart()
    {
        // The [coda] between them isn't a section; the two sections at 1920 are one start.
        var chart = WithSections();
        Add(chart, null, TextEvent.Of(1920, "[prc_verse_1b]"));
        var session = Of(chart);
        session.MoveCursorTo(1920);

        session.Tools.StepCursorSections(1);
        Assert.AreEqual(3840, CursorAt(session));
        session.Tools.StepCursorSections(-1);
        Assert.AreEqual(1920, CursorAt(session));
        session.Tools.StepCursorSections(-1);
        Assert.AreEqual(960, CursorAt(session));
    }

    // Insert section.

    [TestMethod]
    public void InsertSectionAddsASectionAtTheCursorOnTheGridAsOneStep()
    {
        var session = Of(Guitar());
        session.MoveCursorTo(1000);

        var inserted = session.Tools.InsertSection(" Verse 1 ");

        Assert.AreEqual(TextEvent.Of(960, "[section Verse 1]"), inserted);
        Assert.AreSequenceEqual([inserted!.Value], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.IsEmpty(SectionEvents.All(session.Chart));
    }

    [TestMethod]
    [DataRow("[section Intro]")]
    [DataRow("[prc_intro]")]
    public void ASectionWhereThereIsOneAlreadyIsRefused(string existing)
    {
        var chart = Guitar();
        Add(chart, null, TextEvent.Of(960, existing));
        var session = Of(chart);
        session.MoveCursorTo(1000);

        Assert.IsNull(session.Tools.InsertSection("Verse 1"));

        Assert.AreSequenceEqual([TextEvent.Of(960, existing)], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void AnEventThatIsntASectionDoesntStopOneBeingInsertedAtItsTick()
    {
        var chart = Guitar();
        Add(chart, null, TextEvent.Of(960, "[coda]"));
        var session = Of(chart);
        session.MoveCursorTo(960);

        Assert.IsNotNull(session.Tools.InsertSection("Outro"));
        Assert.HasCount(1, SectionEvents.All(session.Chart));
    }

    [TestMethod]
    public void CustomSectionsAreNamedWithTheNextFreeNumber()
    {
        var chart = Guitar();
        Assert.AreEqual("Section", SectionEvents.FreeName(chart, "Section"));
        Add(chart, null, TextEvent.Of(0, "[section Section]"));
        Assert.AreEqual("Section 2", SectionEvents.FreeName(chart, "Section"));
        Add(chart, null, TextEvent.Of(960, "[prc_section_2]"));
        Assert.AreEqual("Section 3", SectionEvents.FreeName(chart, "Section"));
        Assert.AreEqual("Verse 1", SectionEvents.FreeName(chart, "Verse 1"));
    }

    // Renaming.

    [TestMethod]
    [DataRow("[section Chorus 1]", "Chorus 2", "[section Chorus 2]")]
    [DataRow("[prc_chorus_1]", "chorus 2", "[prc_chorus_2]")]
    public void ASectionRenamedKeepsItsStyleAsOneStep(string text, string name, string renamed)
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, text);
        Add(chart, null, section);
        var session = Of(chart);

        var now = session.Tools.RenameSection(section, name);

        Assert.AreEqual(section with { Text = renamed }, now);
        Assert.AreSequenceEqual([now!.Value], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSequenceEqual([section], SectionEvents.All(session.Chart).ToList());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ARenameToNothingIsRefusedInTheStatus(string name)
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Bridge]");
        var free = TextEvent.Of(2880, "[crowd_normal]");
        Add(chart, null, section);
        Add(chart, null, free);
        var session = Of(chart);

        Assert.IsNull(session.Tools.RenameSection(section, name));
        Assert.IsNotNull(session.Status);
        session.Status = null;
        Assert.IsNull(session.Tools.ChangeEventText(null, free, name));
        Assert.IsNotNull(session.Status);

        Assert.AreSequenceEqual([section, free], session.Chart.Events.ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AFreeTextEventsTextIsChangedAsWrittenAsOneStep()
    {
        var chart = Guitar();
        var free = TextEvent.Of(2880, "[crowd_normal]");
        Add(chart, null, free);
        var session = Of(chart);

        var now = session.Tools.ChangeEventText(null, free, " [crowd_intense] ");

        Assert.AreEqual(free with { Text = "[crowd_intense]" }, now);
        Assert.Contains(now!.Value, session.Chart.Events.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ARenameThatChangesNothingIsNoStep()
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Bridge]");
        Add(chart, null, section);
        var session = Of(chart);

        Assert.IsNull(session.Tools.RenameSection(section, "Bridge"));

        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsNull(session.Status);
    }

    // Song events.

    [TestMethod]
    public void InsertingCodaMakesTheDrumFillsAfterItBigRockEndingsInOneStep()
    {
        var chart = Drums();
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 0, 480)).Apply(chart);
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 3840, 960)).Apply(chart);
        var session = Of(chart, focused: DrumsExpert);
        session.MoveCursorTo(1930);

        var coda = session.Tools.InsertEvent(null, SongEvents.TextOf(SongEvent.Coda));

        Assert.AreEqual(TextEvent.Of(1920, "[coda]"), coda);
        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.DrumFill, 0, 480), new Phrase(PhraseKind.BigRockEnding, 3840, 960)],
            session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);

        session.Undo();
        Assert.AreSequenceEqual(
            [new Phrase(PhraseKind.DrumFill, 0, 480), new Phrase(PhraseKind.DrumFill, 3840, 960)],
            session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.ToList());
        Assert.IsNull(DrumFills.FirstCoda(session.Chart));
    }

    [TestMethod]
    public void MovingCodaPastAFillMakesItAFillAgainInTheSameStep()
    {
        var chart = Drums();
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 3840, 960)).Apply(chart);
        var coda = TextEvent.Of(1920, "[coda]");
        Add(chart, null, coda);
        var session = Of(chart, focused: DrumsExpert);
        Assert.AreEqual(PhraseKind.BigRockEnding, session.Chart.PartOf(ChartInstrument.Drums)!.Phrases[0].Kind);

        var moved = session.Tools.MoveEvent(null, coda, 5760);

        Assert.AreEqual(coda with { Tick = 5760 }, moved);
        Assert.AreEqual(PhraseKind.DrumFill, session.Chart.PartOf(ChartInstrument.Drums)!.Phrases[0].Kind);
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DataRow("[end]")]
    [DataRow("[music_start]")]
    [DataRow(" [music_end] ")]
    public void InsertingAPlacedSongEventIsRefused(string text)
    {
        // The editor places [music_start], [music_end] and [end] itself (C8 item 5).
        var session = Of(Guitar());
        session.MoveCursorTo(7680);

        Assert.IsNull(session.Tools.InsertEvent(null, text));

        Assert.IsEmpty(session.Chart.Events);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.Contains(text.Trim(), session.Status!);
        Assert.Contains("placed automatically", session.Status!);
    }

    [TestMethod]
    public void OnlyCodaIsOfferedToInsert() => Assert.AreSequenceEqual([SongEvent.Coda], SongEvents.Insertable.ToList());

    // Moving and deleting.

    [TestMethod]
    public void AnEventIsMovedToTheTickTypedAsOneStep()
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Verse 1]");
        Add(chart, null, section);
        var session = Of(chart);

        var moved = session.Tools.MoveEvent(null, section, 3840);

        Assert.AreEqual(section with { Tick = 3840 }, moved);
        Assert.AreSequenceEqual([moved!.Value], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSequenceEqual([section], SectionEvents.All(session.Chart).ToList());
    }

    [TestMethod]
    public void MovingASectionOntoAnotherOrBeforeTheStartIsRefused()
    {
        var chart = Guitar();
        var verse = TextEvent.Of(1920, "[section Verse 1]");
        var chorus = TextEvent.Of(3840, "[prc_chorus_1]");
        Add(chart, null, verse);
        Add(chart, null, chorus);
        var session = Of(chart);

        Assert.IsNull(session.Tools.MoveEvent(null, verse, 3840));
        Assert.IsNotNull(session.Status);
        session.Status = null;
        Assert.IsNull(session.Tools.MoveEvent(null, verse, -1));
        Assert.IsNotNull(session.Status);

        Assert.AreSequenceEqual([verse, chorus], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AnEventIsDeletedAsOneStep()
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Verse 1]");
        Add(chart, null, section);
        var session = Of(chart);

        Assert.IsTrue(session.Tools.DeleteEvent(null, section));

        Assert.IsEmpty(SectionEvents.All(session.Chart));
        session.Undo();
        Assert.AreSequenceEqual([section], SectionEvents.All(session.Chart).ToList());
    }

    // A part's own text events.

    [TestMethod]
    public void TextEventsGoOnTheFocusedPartsTrackAndAreFreeText()
    {
        // A part's text events are free text: one like a section isn't refused where there's one already.
        var session = Of(Drums(), focused: DrumsExpert);
        session.MoveCursorTo(1000);

        var placeholder = session.Tools.InsertEvent(ChartInstrument.Drums, EditorTools.NewEventText);
        var renamed = session.Tools.ChangeEventText(ChartInstrument.Drums, placeholder!.Value, "[section Verse 1]");
        var second = session.Tools.InsertEvent(ChartInstrument.Drums, "[section Verse 1]");

        Assert.AreEqual(TextEvent.Of(960, "[section Verse 1]"), renamed);
        Assert.AreEqual(TextEvent.Of(960, "[section Verse 1]"), second);
        Assert.AreSequenceEqual([renamed!.Value, second!.Value], session.Chart.PartOf(ChartInstrument.Drums)!.TextEvents.ToList());
        Assert.IsEmpty(SectionEvents.All(session.Chart));
        Assert.AreEqual(3, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void ATextEventOnAPartTheChartHasntIsRefused()
    {
        var session = Of(Guitar());

        Assert.IsNull(session.Tools.InsertEvent(ChartInstrument.Drums, EditorTools.NewEventText));

        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // The Sections tab's rows.

    [TestMethod]
    public void EventsRowsAreEveryEventInOrderEachAsWhatItIs()
    {
        var chart = Guitar();
        Add(chart, null, TextEvent.Of(1920, "[prc_verse_1]"));
        Add(chart, null, TextEvent.Of(960, "[section Intro]"));
        Add(chart, null, TextEvent.Of(2880, "[crowd_normal]"));
        Add(chart, null, TextEvent.Of(3840, "[coda]"));
        Add(chart, null, TextEvent.Of(7680, "[music_end]"));

        var rows = EventRows.Of(chart, null);

        Assert.AreSequenceEqual(
            [
                new EventRow(null, TextEvent.Of(960, "[section Intro]"), SectionName: "Intro"),
                new EventRow(null, TextEvent.Of(1920, "[prc_verse_1]"), SectionName: "verse 1"),
                new EventRow(null, TextEvent.Of(2880, "[crowd_normal]")),
                new EventRow(null, TextEvent.Of(3840, "[coda]"), SongEvent: SongEvent.Coda),
                new EventRow(null, TextEvent.Of(7680, "[music_end]"), SongEvent: SongEvent.MusicEnd),
            ],
            rows.ToList());
        Assert.AreSequenceEqual(
            [EventRowKind.Section, EventRowKind.Section, EventRowKind.Text, EventRowKind.SongEvent, EventRowKind.SongEvent],
            rows.Select(r => r.Kind).ToList());
    }

    [TestMethod]
    public void ThePlacedSongEventsAreListedAfterTheEventsAtTheirTicksInPlaceOfAnyTheChartHas()
    {
        var chart = Guitar();
        Add(chart, null, TextEvent.Of(0, "[section Intro]"));
        Add(chart, null, TextEvent.Of(1920, "[crowd_normal]"));
        Add(chart, null, TextEvent.Of(3840, "[music_end]"));

        var rows = EventRows.Of(chart, null, new SongEventMarks(0, 1920, 3840, FromAudio: true));

        Assert.AreSequenceEqual(
            [
                new EventRow(null, TextEvent.Of(0, "[section Intro]"), SectionName: "Intro"),
                new EventRow(null, TextEvent.Of(0, "[music_start]"), SongEvent: SongEvent.MusicStart, IsPlaced: true),
                new EventRow(null, TextEvent.Of(1920, "[crowd_normal]")),
                new EventRow(null, TextEvent.Of(1920, "[music_end]"), SongEvent: SongEvent.MusicEnd, IsPlaced: true),
                new EventRow(null, TextEvent.Of(3840, "[end]"), SongEvent: SongEvent.End, IsPlaced: true),
            ],
            rows.ToList());
    }

    [TestMethod]
    public void APartsRowsAreItsOwnTextEventsAllFreeText()
    {
        var chart = Drums();
        Add(chart, ChartInstrument.Drums, TextEvent.Of(960, "[section Verse 1]"));
        Add(chart, ChartInstrument.Drums, TextEvent.Of(0, "[idle]"));

        var rows = EventRows.Of(chart, ChartInstrument.Drums);

        Assert.AreSequenceEqual(
            [new EventRow(ChartInstrument.Drums, TextEvent.Of(0, "[idle]")), new EventRow(ChartInstrument.Drums, TextEvent.Of(960, "[section Verse 1]"))],
            rows.ToList());
        Assert.IsTrue(rows.All(r => r.Kind == EventRowKind.Text));
        Assert.IsEmpty(EventRows.Of(chart, ChartInstrument.Guitar));
    }

    [TestMethod]
    [DataRow("[coda]", SongEvent.Coda)]
    [DataRow("[music_start]", SongEvent.MusicStart)]
    [DataRow(" [music_end] ", SongEvent.MusicEnd)]
    [DataRow("[end]", SongEvent.End)]
    public void SongEventsAreReadFromTheirText(string text, SongEvent kind)
    {
        Assert.AreEqual(kind, SongEvents.KindOf(text));
        Assert.AreEqual(kind, SongEvents.KindOf(SongEvents.TextOf(kind)));
    }

    [TestMethod]
    [DataRow("[section End]")]
    [DataRow("[ending]")]
    [DataRow("[prc_coda]")]
    public void OtherTextIsntASongEvent(string text) => Assert.IsNull(SongEvents.KindOf(text));

    // Duplicates a loaded chart already has.

    [TestMethod]
    public void ASectionSharingItsTickWithAnotherCanStillBeRenamedAndMoved()
    {
        var chart = Guitar();
        var verse = TextEvent.Of(1920, "[section Verse 1]");
        var other = TextEvent.Of(1920, "[prc_verse_1b]");
        Add(chart, null, verse);
        Add(chart, null, other);
        var session = Of(chart);

        var renamed = session.Tools.RenameSection(verse, "Verse 2");
        var moved = session.Tools.MoveEvent(null, other, 3840);

        Assert.AreEqual(verse with { Text = "[section Verse 2]" }, renamed);
        Assert.AreEqual(other with { Tick = 3840 }, moved);
        Assert.IsNull(session.Status);
        Assert.AreSequenceEqual([renamed!.Value, moved!.Value], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(2, session.UndoStack.UndoCount);

        // Making a pair again is still refused.
        Assert.IsNull(session.Tools.MoveEvent(null, moved.Value, 1920));
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(2, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DataRow("[end]")]
    [DataRow("[music_start]")]
    [DataRow("[music_end]")]
    public void AFreeTextEventRenamedIntoAPlacedSongEventIsRefused(string text)
    {
        var chart = Guitar();
        var free = TextEvent.Of(2880, "[crowd_normal]");
        Add(chart, null, free);
        var session = Of(chart);
        var before = session.Chart.Events.ToList();

        Assert.IsNull(session.Tools.ChangeEventText(null, free, text));

        Assert.Contains(text, session.Status!);
        Assert.AreSequenceEqual(before, [.. session.Chart.Events]);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void OfTwoIdenticalEventsAtATickARenameOrADeleteChangesOne()
    {
        var chart = Guitar();
        var crowd = TextEvent.Of(960, "[crowd_normal]");
        Add(chart, null, crowd);
        Add(chart, null, crowd);
        var session = Of(chart);

        Assert.AreEqual(crowd with { Text = "[crowd_intense]" }, session.Tools.ChangeEventText(null, crowd, "[crowd_intense]"));
        Assert.AreSequenceEqual(["[crowd_normal]", "[crowd_intense]"], session.Chart.Events.At(960).ToArray().Select(e => e.Text).ToList());

        session.Undo();
        Assert.IsTrue(session.Tools.DeleteEvent(null, crowd));
        Assert.AreSequenceEqual([crowd], session.Chart.Events.At(960).ToArray().ToList());
    }

    [TestMethod]
    public void DeletingCodaMakesTheBigRockEndingsAfterItFillsAgainInOneStep()
    {
        var chart = Drums();
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.DrumFill, 3840, 960)).Apply(chart);
        var coda = TextEvent.Of(1920, "[coda]");
        Add(chart, null, coda);
        var session = Of(chart, focused: DrumsExpert);
        Assert.AreEqual(PhraseKind.BigRockEnding, session.Chart.PartOf(ChartInstrument.Drums)!.Phrases[0].Kind);

        Assert.IsTrue(session.Tools.DeleteEvent(null, coda));

        Assert.AreSequenceEqual([new Phrase(PhraseKind.DrumFill, 3840, 960)], session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.ToList());
        Assert.IsNull(DrumFills.FirstCoda(session.Chart));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.AreSequenceEqual([new Phrase(PhraseKind.BigRockEnding, 3840, 960)], session.Chart.PartOf(ChartInstrument.Drums)!.Phrases.ToList());
    }

    // How far into the song an event can go.

    [TestMethod]
    public void ATickFarPastTheEndOfTheSongIsRefused()
    {
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Verse 1]");
        Add(chart, null, section);
        var session = Of(chart);

        Assert.IsNull(session.Tools.MoveEvent(null, section, 99_999_999_999));
        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);

        // An hour of 120 BPM at 480 ticks a beat is 3,456,000 ticks, so a song's worth of ticks before it is fine.
        Assert.AreEqual(section with { Tick = 3_000_000 }, session.Tools.MoveEvent(null, section, 3_000_000));
        Assert.IsNull(session.Tools.MoveEvent(null, TextEvent.Of(3_000_000, section.Text), 3_500_000));
    }

    [TestMethod]
    public void TheLatestTickFollowsAChartThatRunsLongerThanAnHour()
    {
        // 64 measures of 4/4 past the chart's last event: 5,000,000 + 122,880.
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Verse 1]");
        Add(chart, null, section);
        Add(chart, null, TextEvent.Of(5_000_000, "[crowd_normal]"));
        var session = Of(chart);

        Assert.IsNull(session.Tools.MoveEvent(null, section, 5_122_881));
        Assert.AreEqual(section with { Tick = 5_122_880 }, session.Tools.MoveEvent(null, section, 5_122_880));
    }

    [TestMethod]
    public void InsertingFarPastTheEndOfTheSongIsRefused()
    {
        var session = Of(Guitar());
        session.MoveCursorTo(10_000_000);

        Assert.IsNull(session.Tools.InsertSection("Outro"));
        Assert.IsNull(session.Tools.InsertEvent(ChartInstrument.Guitar, EditorTools.NewEventText));

        Assert.IsNotNull(session.Status);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // Brackets in a section's name.

    [TestMethod]
    [DataRow("Solo [lead]")]
    [DataRow("Solo]")]
    [DataRow("[Solo")]
    public void ASectionNameWithABracketIsRefused(string name)
    {
        // [section Solo [lead]] would read back as "Solo [lead": the event's brackets end at the first ].
        var chart = Guitar();
        var section = TextEvent.Of(1920, "[section Solo]");
        Add(chart, null, section);
        var session = Of(chart);
        session.MoveCursorTo(3840);

        Assert.IsNull(session.Tools.InsertSection(name));
        Assert.IsNotNull(session.Status);
        session.Status = null;
        Assert.IsNull(session.Tools.RenameSection(section, name));
        Assert.IsNotNull(session.Status);

        Assert.AreSequenceEqual([section], SectionEvents.All(session.Chart).ToList());
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    // Measure and beat, as the rows show them.

    [TestMethod]
    public void MeasureAndBeatAreCountedAsTheBeatLinesAreDrawn()
    {
        // Signatures on and off the measure lines, and with eighth-note beats.
        var map = new TempoMap(
            480,
            [new TempoChange(0, 500_000), new TempoChange(7000, 400_000)],
            [new TimeSignature(0, 4, 4), new TimeSignature(4000, 3, 4), new TimeSignature(9000, 6, 8), new TimeSignature(20000, 7, 8)]);

        for (long tick = 0; tick <= 30000; tick += 37)
            Assert.AreEqual(Walked(map, tick), MeasureBeat.At(map, tick), $"tick {tick}");
        foreach (long tick in new long[] { 1920, 3999, 4000, 4001, 8999, 9000, 19999, 20000 })
            Assert.AreEqual(Walked(map, tick), MeasureBeat.At(map, tick), $"tick {tick}");
        Assert.AreEqual(new MeasureBeat(2, 2), MeasureBeat.At(map, 1920 + 480));
    }

    [TestMethod]
    public void MeasureAndBeatFarIntoTheSongAreWorkedOutWithoutWalkingThere()
    {
        var map = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);

        Assert.AreEqual(new MeasureBeat(52_083_334, 3), MeasureBeat.At(map, (52_083_333L * 1920) + 960));
    }

    /// <summary>Where <paramref name="tick"/> is, by walking every beat line up to it, as Properties once did.</summary>
    private static MeasureBeat Walked(TempoMap map, long tick)
    {
        long measure = 0, measureStart = 0;
        foreach (var line in map.BeatLines(0, tick))
        {
            if (line.IsMeasure)
            {
                measure++;
                measureStart = line.Tick;
            }
        }

        var signature = map.TimeSignatures.Last(s => s.Tick <= tick);
        return new MeasureBeat(Math.Max(1, measure), 1 + ((tick - measureStart) / (map.Resolution * 4.0 / signature.Denominator)));
    }

    /// <summary>A Lead guitar chart with sections at 960 ([section …]), 1920 and 3840 ([prc_…]), and [coda] at 2880.</summary>
    private static EditableChart WithSections()
    {
        var chart = Guitar();
        Add(chart, null, TextEvent.Of(960, "[section Intro]"));
        Add(chart, null, TextEvent.Of(1920, "[prc_verse_1]"));
        Add(chart, null, TextEvent.Of(2880, "[coda]"));
        Add(chart, null, TextEvent.Of(3840, "[section Chorus 1]"));
        return chart;
    }

    private static void Add(EditableChart chart, ChartInstrument? part, TextEvent textEvent) => new AddTextEvent(part, textEvent).Apply(chart);

    private static long CursorAt(EditorSession session) => (long)Math.Round(session.TickAt(session.Viewport.Cursor));
}
