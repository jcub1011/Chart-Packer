using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartMidiTests
{
    [TestMethod]
    [DataRow(192)]
    [DataRow(480)]
    [DataRow(960)]
    public void ThePlaceholderReadsAsAnEmptyChartAtTheResolution(int resolution)
    {
        var settings = new NewChartSettings("Song", resolution, 140, 3, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro);

        var chart = ChartReader.Read(NewChartMidi.Build(settings), "notes.mid");

        Assert.AreEqual(resolution, chart.Tempo.Resolution);
        Assert.IsEmpty(chart.Tracks);
    }
}
