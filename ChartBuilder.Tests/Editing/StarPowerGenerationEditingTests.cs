using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// A run of Generate star power in the editor (charter-spec C8 item 1): the proposals kept or dropped, by their ghosts in
/// the phrase rail or all at once, then added as one undoable step or cancelled; a run ended by a change to the chart, or
/// by a run of Generate fills; and where it's refused.
/// </summary>
[TestClass]
public sealed class StarPowerGenerationEditingTests
{
    private const long Measure = 1920;

    [TestMethod]
    public void ARunProposesStarPowerEachKeptAndChangesNothing()
    {
        var session = Session();

        Assert.IsTrue(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null));

        Assert.AreSame(session.StarPower, session.Proposals);
        Assert.AreEqual(ChartInstrument.Guitar, session.StarPower.ProposedPart);
        Assert.HasCount(6, session.StarPower.Proposals);
        Assert.AreEqual(6, session.StarPower.KeptCount);
        Assert.AreEqual(0, session.UndoStack.UndoCount);
        Assert.IsEmpty(StarPower(session));
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DataRow(LaneView.Highway)]
    [DataRow(LaneView.Timeline)]
    public void ClickingAGhostInTheStarPowerRowKeepsOrDropsIt(LaneView view)
    {
        // With the Phrase tool drawing star power, a click on a ghost still only drops it.
        var session = Session();
        session.Tool = EditorTool.Phrase;
        session.PhraseKind = PhraseKind.StarPower;
        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);
        var second = session.StarPower.Proposals[1].Phrase;

        Click(session, RailAt(session, second.Tick + 200, PhraseKind.StarPower, view, GuitarExpert));

        Assert.IsFalse(session.StarPower.IsKept(1));
        Assert.AreEqual(5, session.StarPower.KeptCount);
        Assert.AreEqual(0, session.UndoStack.UndoCount);

        Click(session, RailAt(session, second.Tick + 200, PhraseKind.StarPower, view, GuitarExpert));
        Assert.IsTrue(session.StarPower.IsKept(1));
        Assert.IsNull(session.StarPower.HitTest(RailAt(session, second.Tick + 200, PhraseKind.Solo, view, GuitarExpert)));
    }

    [TestMethod]
    public void TheEraseToolKeepsOrDropsAGhostRatherThanErasingUnderIt()
    {
        var session = Session();
        session.Tool = EditorTool.Erase;
        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);
        var second = session.StarPower.Proposals[1].Phrase;

        Click(session, RailAt(session, second.Tick + 200, PhraseKind.StarPower, key: GuitarExpert));

        Assert.IsFalse(session.StarPower.IsKept(1));
        Assert.AreSame(session.StarPower, session.Proposals, "The run goes on.");
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void AcceptingAddsOnlyTheKeptPhrasesAsOneStepAndUndoRemovesThemAll()
    {
        var session = Session();
        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);
        var proposals = session.StarPower.Proposals.Select(p => p.Phrase).ToList();
        session.StarPower.Toggle(0);

        Assert.IsTrue(session.StarPower.Accept());

        Assert.AreSequenceEqual(proposals.Skip(1).ToList(), StarPower(session).ToList());
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        Assert.IsFalse(session.StarPower.IsActive);
        Assert.IsNull(session.Proposals);

        session.Undo();
        Assert.IsEmpty(StarPower(session));
    }

    [TestMethod]
    public void AChangeToTheChartEndsTheRunAndSaysSo()
    {
        var session = Session();
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);
        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);

        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(240, 1, 4)]));

        Assert.IsFalse(session.StarPower.IsActive);
        Assert.HasCount(1, said);
        Assert.Contains("dropped", said[0]);
    }

    [TestMethod]
    public void ARunOfGenerateFillsEndsARunOfStarPowerAndTheOtherWayRound()
    {
        var session = Session(withDrums: true);
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        for (long m = 0; m <= 20; m++)
            drums[ChartDifficulty.Expert].Notes.Add(new EditableNote(m * Measure, 1, 4));
        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 960));
        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, Measure, 960));
        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);

        Assert.IsTrue(session.Fills.Start(FillOptions.Default, null));
        Assert.IsFalse(session.StarPower.IsActive);
        Assert.AreSame(session.Fills, session.Proposals);

        Assert.IsTrue(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null));
        Assert.IsFalse(session.Fills.IsActive);
        Assert.AreSame(session.StarPower, session.Proposals);
    }

    [TestMethod]
    public void AWholePartWithStarPowerIsRefusedButASpanOfItIsnt()
    {
        var session = Session();
        new AddPhrase(ChartInstrument.Guitar, new Phrase(PhraseKind.StarPower, 20 * Measure, Measure)).Apply(session.Chart);

        Assert.IsFalse(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null));
        Assert.Contains("already", session.Status ?? string.Empty);

        Assert.IsTrue(session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, (30 * Measure, (56 * Measure) - 1)));
        Assert.IsTrue(session.StarPower.Proposals.All(p => p.Phrase.Tick >= 30 * Measure && p.Phrase.CoverEnd <= 56 * Measure));
    }

    [TestMethod]
    public void DrumsAreRefusedAndSaySo()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 1));

        Assert.IsFalse(session.StarPower.Start(ChartInstrument.Drums, StarPowerOptions.Default, null));

        Assert.IsNull(session.Proposals);
        Assert.Contains("5-fret", session.Status ?? string.Empty);
    }

    /// <summary>
    /// A session over Lead guitar with quarter notes on Expert for 64 measures (128 s): six phrases fit. With
    /// <paramref name="withDrums"/>, Pro drums too, with no notes.
    /// </summary>
    private static EditorSession Session(bool withDrums = false)
    {
        var parts = new HashSet<ChartInstrument> { ChartInstrument.Guitar };
        if (withDrums)
            parts.Add(ChartInstrument.Drums);
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, parts, DrumsVariant.Pro));
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < 4; q++)
                expert.Notes.Add(new EditableNote((m * Measure) + (q * 480), 1, q % 3));
        }

        return Of(chart);
    }

    private static IEnumerable<Phrase> StarPower(EditorSession session) =>
        session.Chart.PartOf(ChartInstrument.Guitar)!.Phrases.Where(static p => p.Kind == PhraseKind.StarPower);
}
