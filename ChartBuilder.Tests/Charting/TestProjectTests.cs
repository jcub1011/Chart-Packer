using ChartPacker.Core.Charts;
using ChartPacker.Core.Tests.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class TestProjectTests
{
    // The project builds MIDI fixtures with ChartPacker.Core.Tests' builder, linked in, and reads them with Core's
    // internals.
    [TestMethod]
    public void FixturesBuiltWithTheSharedBuilderAreReadable()
    {
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Build();

        var (format, division, chunks) = MidiFile.ReadChunks(midi);

        Assert.AreEqual(1, format);
        Assert.AreEqual(480, division);
        Assert.HasCount(2, chunks);
    }
}
