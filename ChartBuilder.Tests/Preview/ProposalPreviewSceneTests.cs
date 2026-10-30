using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Preview;

/// <summary>The generation window's highway preview: the kept phrases as bands and, for star power, the notes they tint.</summary>
[TestClass]
public sealed class ProposalPreviewSceneTests
{
    private const long Beat = 480;
    private const long Measure = 1920;

    [TestMethod]
    public void TheKeptPhrasesAreBandsAndTheNotesTheyCoverAreStarPowered()
    {
        var chart = Guitar();
        var expert = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        for (int i = 0; i < 16; i++)
            expert.Notes.Add(new EditableNote(i * Beat, 1, i % 3));
        var session = Of(chart);
        var playing = new[] { new Phrase(PhraseKind.StarPower, 0, Measure), new Phrase(PhraseKind.StarPower, 8 * Beat, 2 * Beat) };

        var scene = ProposalPreviewScene.Of(session, GuitarExpert, playing, starPower: true);
        var lane = scene.Lanes.Single();

        Assert.HasCount(2, lane.Bands);
        Assert.AreEqual(HighwayBandKind.StarPower, lane.Bands[0].Kind);
        Assert.AreEqual(session.AudioAt(0), lane.Bands[0].Start, 1e-9);
        Assert.AreEqual(session.AudioAt(Measure), lane.Bands[0].End, 1e-9);
        Assert.AreEqual(session.AudioAt(8 * Beat), lane.Bands[1].Start, 1e-9);
        Assert.IsTrue(Covered(lane, 0 * Beat));
        Assert.IsTrue(Covered(lane, 3 * Beat));
        Assert.IsFalse(Covered(lane, 4 * Beat));
        Assert.IsTrue(Covered(lane, 8 * Beat));
        Assert.IsFalse(Covered(lane, 10 * Beat));
    }

    [TestMethod]
    public void TheChartsOwnStarPowerIsTintedOnlyWhereTheResultKeepsIt()
    {
        var chart = Guitar();
        var part = chart.PartOf(ChartInstrument.Guitar)!;
        var expert = part[ChartDifficulty.Expert];
        for (int i = 0; i < 32; i++)
            expert.Notes.Add(new EditableNote(i * Beat, 1, i % 3));
        part.Phrases.Add(new Phrase(PhraseKind.StarPower, 16 * Beat, Measure));
        var session = Of(chart);

        var scene = ProposalPreviewScene.Of(session, GuitarExpert, [new Phrase(PhraseKind.StarPower, 0, Measure)], starPower: true);
        var lane = scene.Lanes.Single();

        Assert.IsTrue(Covered(lane, 0));
        Assert.IsFalse(Covered(lane, 16 * Beat), "The chart's own phrase isn't tinted where the result doesn't keep it.");
    }

    [TestMethod]
    public void AFillsBandsAreProposalBandsAndTheNotesKeepTheChartsColors()
    {
        var chart = Drums();
        var drums = chart.PartOf(ChartInstrument.Drums)!;
        var expert = drums[ChartDifficulty.Expert];
        expert.Notes.Add(new EditableNote(0, 1, 4));
        drums.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, Measure));
        var session = Of(chart, focused: DrumsExpert);

        var scene = ProposalPreviewScene.Of(session, DrumsExpert, [new Phrase(PhraseKind.DrumFill, 0, Measure)], starPower: false);
        var lane = scene.Lanes.Single();

        Assert.HasCount(1, lane.Bands);
        Assert.AreEqual(HighwayBandKind.Proposal, lane.Bands[0].Kind);
        Assert.IsTrue((lane.Notes[0].Flags & NoteFlags.StarPower) != 0, "A fill preview leaves the chart's star power as it is.");
    }

    private static bool Covered(HighwayLane lane, long tick) =>
        (lane.Notes.Single(n => n.Tick == tick).Flags & NoteFlags.StarPower) != 0;
}
