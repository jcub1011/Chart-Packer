using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Preview;

/// <summary>
/// The phrases a highway draws behind its notes: which of a part's phrases a difficulty plays through, and the bands they
/// make, timed as the notes are.
/// </summary>
[TestClass]
public sealed class PhraseBandsTests
{
    private static EditableChart DrumChart()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, DrumsVariant.Pro));
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.StarPower, 0, 960)).Apply(chart);
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.Solo, 1920, 480)).Apply(chart);
        new AddPhrase(ChartInstrument.Drums, new Phrase(PhraseKind.Roll, 4800, 480, ChartDifficulty.Hard)).Apply(chart);
        return chart;
    }

    [TestMethod]
    public void ADifficultyPlaysThroughEveryPhraseAndARollOnlyDownToItsLowestDifficulty()
    {
        var chart = DrumChart();

        var expert = PhraseSpans.Of(chart, new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert));
        CollectionAssert.AreEqual(
            new PhraseKind?[] { PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.Roll },
            expert.Select(s => s.Kind).ToArray());

        var hard = PhraseSpans.Of(chart, new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard));
        Assert.HasCount(3, hard, "A roll reaches Hard, where it's lowest.");

        var medium = PhraseSpans.Of(chart, new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Medium));
        CollectionAssert.AreEqual(
            new PhraseKind?[] { PhraseKind.StarPower, PhraseKind.Solo },
            medium.Select(s => s.Kind).ToArray(),
            "A roll doesn't reach below its lowest difficulty.");
    }

    [TestMethod]
    public void ASpanEndsWhereThePhraseStopsCoveringTicks()
    {
        var spans = PhraseSpans.Of(DrumChart(), new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert));

        Assert.AreEqual(0, spans[0].Tick);
        Assert.AreEqual(960, spans[0].EndTick);
        Assert.IsTrue(spans[0].HasEnd);
    }

    [TestMethod]
    public void APartTheChartHasNotHasNoSpans()
    {
        Assert.IsEmpty(PhraseSpans.Of(DrumChart(), new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert)));
    }

    [TestMethod]
    public void ABandIsTimedByTheTempoAndDelayAsTheNotesAre()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);

        var solo = HighwayBand.Of(new PhraseSpan(PhraseKind.Solo, 960, 1920), tempo, TimeSpan.FromSeconds(0.25));

        Assert.AreEqual(HighwayBandKind.Solo, solo.Kind);
        Assert.AreEqual(1.25, solo.Start, 1e-9, "Tick 960 is one second at 120 BPM, and the chart plays a quarter second late.");
        Assert.AreEqual(2.25, solo.End, 1e-9);
        Assert.IsTrue(solo.HasEnd);
    }

    [TestMethod]
    public void ADiscoFlipSectionStillOnHasNoEnd()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);

        var flip = HighwayBand.Of(new PhraseSpan(null, 480, 480, HasEnd: false), tempo, TimeSpan.Zero);

        Assert.AreEqual(HighwayBandKind.DiscoFlip, flip.Kind);
        Assert.IsFalse(flip.HasEnd);
        Assert.AreEqual(0.5, flip.Start, 1e-9);
    }

    [TestMethod]
    public void ALaneDrawsItsBandsThenTheChartsPhrases()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        var lane = new HighwayLane(
            ChartInstrumentKind.FiveFret, 5, "Expert", [new ChartNote(0, 0, 0, NoteFlags.None)], tempo, TimeSpan.Zero,
            bands: [new HighwayBand(1, 2, HighwayBandKind.Proposal)],
            phrases: [new PhraseSpan(PhraseKind.StarPower, 480, 960)]);

        Assert.AreEqual(2, lane.Bands.Length);
        Assert.AreEqual(HighwayBandKind.Proposal, lane.Bands[0].Kind, "A generator window's selected band keeps its place.");
        Assert.AreEqual(HighwayBandKind.StarPower, lane.Bands[1].Kind);
    }

    [TestMethod]
    public void ASongsChartBytesGiveTheSpansOfEveryTrackWithNotes()
    {
        var chart = DrumChart();
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(0, 0, 1), new EditableNote(960, 0, 2)]).Apply(chart);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Hard, [new EditableNote(0, 0, 1)]).Apply(chart);

        var tracks = PhraseSpans.Read("notes.mid", MidiChartWriter.Write(chart), []);

        CollectionAssert.AreEquivalent(
            new[] { new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert), new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard) },
            tracks.Keys.ToArray(),
            "A track with no notes isn't shown.");
        var expert = tracks[new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert)];
        Assert.IsTrue(expert.Any(s => s.Kind == PhraseKind.StarPower && s.Tick == 0), "Read back from the MIDI as the games read it.");
        Assert.IsTrue(expert.Any(s => s.Kind == PhraseKind.Solo));
        Assert.IsTrue(expert.Any(s => s.Kind == PhraseKind.Roll));
        Assert.IsFalse(tracks[new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard)].Any(s => s.Kind == PhraseKind.Roll && s.Tick != 4800), "Only the roll that was written.");
    }

    [TestMethod]
    public void AChartThatCannotBeReadHasNoSpansRatherThanFailing()
    {
        Assert.IsEmpty(PhraseSpans.Read("notes.mid", [1, 2, 3], []));
    }
}
