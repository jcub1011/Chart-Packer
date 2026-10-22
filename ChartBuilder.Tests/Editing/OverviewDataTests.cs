using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// What the bottom timeline shows of the whole song (charter-spec C3 item 2): the notes of the lane in focus, in its rows
/// and colours, every one of its phrases, in the row of the phrase rail it's drawn in, and the ghosts of a run of Generate
/// fills or Generate star power. At 480 ticks a beat and 120 BPM, a beat is half a second.
/// </summary>
[TestClass]
public sealed class OverviewDataTests
{
    private const long Beat = 480, Measure = 1920;
    private const double Length = 300;

    [TestMethod]
    public void EveryDrumsPhraseIsInItsRowOfTheRail()
    {
        var chart = Drums();
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 1));
        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, Beat));
        drums.Phrases.Add(new Phrase(PhraseKind.Solo, Measure, Beat));
        drums.Phrases.Add(new Phrase(PhraseKind.DrumFill, 2 * Measure, Beat));
        drums.Phrases.Add(new Phrase(PhraseKind.Roll, 3 * Measure, Beat, ChartDifficulty.Expert));
        drums.Phrases.Add(new Phrase(PhraseKind.Trill, 4 * Measure, Beat, ChartDifficulty.Expert));
        drums.Phrases.Add(new Phrase(PhraseKind.BigRockEnding, 5 * Measure, Beat));
        new AddDiscoFlip(ChartDifficulty.Expert, 6 * Measure, 7 * Measure).Apply(chart);

        var overview = Overview(Of(chart, focused: DrumsExpert), DrumsExpert);

        Assert.AreSequenceEqual(PhraseRail.RowsOf(ChartInstrumentKind.Drums).ToList(), overview.Rows.ToList());
        var rows = overview.Phrases.OrderBy(p => p.Start).Select(p => p.Row).ToList();
        RailKind fill = PhraseKind.DrumFill;
        Assert.AreSequenceEqual(
            [PhraseKind.StarPower, PhraseKind.Solo, fill, PhraseKind.Roll, PhraseKind.Trill, fill, RailKind.DiscoFlip],
            rows,
            "A drums big rock ending is in the fills' row, as the rail draws it.");
        var flip = overview.Phrases.Single(p => p.Row == RailKind.DiscoFlip);
        Assert.AreEqual(12, flip.Start, 1e-9);
        Assert.AreEqual(14, flip.End, 1e-9);
    }

    [TestMethod]
    public void AGuitarsRowsAreStarPowerSoloAndBigRockEnding()
    {
        var chart = Guitar();
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 0));
        guitar.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 2 * Beat));
        guitar.Phrases.Add(new Phrase(PhraseKind.Solo, Measure, Beat));
        guitar.Phrases.Add(new Phrase(PhraseKind.BigRockEnding, 2 * Measure, Beat));

        var overview = Overview(Of(chart), GuitarExpert);

        Assert.AreSequenceEqual(PhraseRail.RowsOf(ChartInstrumentKind.FiveFret).ToList(), overview.Rows.ToList());
        Assert.AreSequenceEqual(
            [new OverviewPhrase(0, 1, PhraseKind.StarPower), new OverviewPhrase(2, 2.5, PhraseKind.Solo), new OverviewPhrase(4, 4.5, PhraseKind.BigRockEnding)],
            overview.Phrases.OrderBy(p => p.Start).ToList());
    }

    [TestMethod]
    public void ARunsProposalsShowKeptAndDroppedWhicheverLaneIsInFocus()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < 4; q++)
                expert.Notes.Add(new EditableNote((m * Measure) + (q * Beat), 1, q % 3));
        }

        var session = Of(chart, focused: DrumsExpert);
        Assert.IsEmpty(Overview(session, DrumsExpert).Proposals);

        session.StarPower.Start(ChartInstrument.Guitar, StarPowerOptions.Default, null);
        session.StarPower.Toggle(0);
        var overview = Overview(session, DrumsExpert);

        Assert.HasCount(session.StarPower.Proposals.Count, overview.Proposals);
        Assert.IsFalse(overview.Proposals[0].Kept);
        Assert.IsTrue(overview.Proposals.Skip(1).All(p => p.Kept));
        var first = session.StarPower.Proposals[0].Phrase;
        Assert.AreEqual(session.AudioAt(first.Tick), overview.Proposals[0].Start, 1e-9);
        Assert.AreEqual(session.AudioAt(first.CoverEnd), overview.Proposals[0].End, 1e-9);
    }

    [TestMethod]
    public void AGuitarsNotesAreInTheirFretsRowsAndColours()
    {
        var chart = Guitar();
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        expert.Notes.Add(new EditableNote(0, 1, 1));
        expert.Notes.Add(new EditableNote(Beat, Beat, 3));
        expert.Notes.Add(new EditableNote(Measure, 1, ChartNote.OpenLane));

        var overview = Overview(Of(chart), GuitarExpert);

        Assert.AreEqual(5, overview.NoteRows);
        Assert.HasCount(3, overview.Notes);
        var (red, blue, open) = (overview.Notes[0], overview.Notes[1], overview.Notes[2]);
        Assert.AreEqual((1, HighwayPalette.Red), (red.Row, red.Color));
        Assert.AreEqual(0, red.Start, 1e-9);
        Assert.AreEqual((3, HighwayPalette.Blue), (blue.Row, blue.Color));
        Assert.AreEqual(0.5, blue.Start, 1e-9);
        Assert.AreEqual(1, blue.End, 1e-9, "A sustain's note ends where its sustain does.");
        Assert.AreEqual((-1, HighwayPalette.Open), (open.Row, open.Color), "An open note is across every row.");
        Assert.AreEqual(2, open.Start, 1e-9);
    }

    [TestMethod]
    [DataRow(DrumsVariant.Pro, 4)]
    [DataRow(DrumsVariant.FiveLane, 5)]
    public void ADrumsKickIsAcrossEveryPadsRow(DrumsVariant variant, int pads)
    {
        var chart = Drums(variant);
        var expert = chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0));
        expert.Notes.Add(new EditableNote(Beat, 1, 1));

        var overview = Overview(Of(chart, focused: DrumsExpert), DrumsExpert);

        Assert.AreEqual(pads, overview.NoteRows);
        Assert.AreSequenceEqual([(-1, HighwayPalette.Kick), (0, HighwayPalette.Red)], overview.Notes.Select(n => (n.Row, n.Color)).ToList());
    }

    [TestMethod]
    public void ASixFretChordsBlackFretIsInTheTopHalfOfItsRow()
    {
        var chart = Ghl();
        var expert = chart.PartOf(ChartInstrument.GhlGuitar)![ChartDifficulty.Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0));
        expert.Notes.Add(new EditableNote(0, 1, 3));
        expert.Notes.Add(new EditableNote(Beat, 1, 4));

        var overview = Overview(Of(chart, focused: GhlExpert), GhlExpert);

        Assert.AreEqual(3, overview.NoteRows);
        Assert.AreSequenceEqual(
            [(0.0, 0, HighwayPalette.WhiteFret, false), (0.0, 0, HighwayPalette.BlackFretOutline, true), (0.5, 1, HighwayPalette.BlackFretOutline, false)],
            overview.Notes.Select(n => (n.Start, n.Row, n.Color, n.Top)).OrderBy(n => n.Start).ThenBy(n => n.Top).ToList(),
            "A black fret played with a white one is in the white's row, in its top half; one on its own isn't.");
    }

    [TestMethod]
    public void OnlyTheLaneInFocusesNotesAreShown()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 0));
        guitar[ChartDifficulty.Hard].Notes.Add(new EditableNote(Beat, 1, 1));
        chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.Add(new EditableNote(Measure, 1, 2));
        var guitarHard = GuitarExpert with { Difficulty = ChartDifficulty.Hard };

        Assert.AreSequenceEqual([0.0], Overview(Of(chart), GuitarExpert).Notes.Select(n => n.Start).ToList());
        Assert.AreSequenceEqual([0.5], Overview(Of(chart, focused: guitarHard), guitarHard).Notes.Select(n => n.Start).ToList());
        Assert.AreSequenceEqual([2.0], Overview(Of(chart, focused: DrumsExpert), DrumsExpert).Notes.Select(n => n.Start).ToList());
    }

    [TestMethod]
    public void NothingIsShownUntilTheSongsLengthIsKnown() =>
        Assert.IsNull(OverviewData.Of(Of(Guitar()), GuitarExpert, 0));

    private static OverviewData Overview(EditorSession session, ChartTrackKey key) => OverviewData.Of(session, key, Length)!;
}
