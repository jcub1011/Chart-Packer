using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// What the bottom timeline shows of the whole song (charter-spec C3 item 2): every phrase of the lane in focus, in the
/// row of the phrase rail it's drawn in, and the ghosts of a run of Generate fills or Generate star power. At 480 ticks a
/// beat and 120 BPM, a beat is half a second.
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
    public void NothingIsShownUntilTheSongsLengthIsKnown() =>
        Assert.IsNull(OverviewData.Of(Of(Guitar()), GuitarExpert, 0, 600));

    private static OverviewData Overview(EditorSession session, ChartTrackKey key) => OverviewData.Of(session, key, Length, 600)!;
}
