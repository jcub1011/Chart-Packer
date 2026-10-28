using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Preview;

/// <summary>The star power window's highway preview: the kept phrases and the notes they tint, without changing the chart.</summary>
[TestClass]
public sealed class StarPowerPreviewSceneTests
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

        var scene = StarPowerPreviewScene.Of(session, GuitarExpert, playing);
        var lane = scene.Lanes.Single();

        Assert.HasCount(2, lane.StarPowerBands);
        Assert.AreEqual(session.AudioAt(0), lane.StarPowerBands[0].Start, 1e-9);
        Assert.AreEqual(session.AudioAt(Measure), lane.StarPowerBands[0].End, 1e-9);
        Assert.AreEqual(session.AudioAt(8 * Beat), lane.StarPowerBands[1].Start, 1e-9);
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

        var scene = StarPowerPreviewScene.Of(session, GuitarExpert, [new Phrase(PhraseKind.StarPower, 0, Measure)]);
        var lane = scene.Lanes.Single();

        Assert.IsTrue(Covered(lane, 0));
        Assert.IsFalse(Covered(lane, 16 * Beat), "The chart's own phrase isn't tinted where the result doesn't keep it.");
    }

    private static bool Covered(ChartShared.UI.Controls.Highway.HighwayLane lane, long tick) =>
        (lane.Notes.Single(n => n.Tick == tick).Flags & NoteFlags.StarPower) != 0;
}
