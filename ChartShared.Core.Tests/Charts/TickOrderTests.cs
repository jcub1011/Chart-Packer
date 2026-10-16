using System.Text;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class TickOrderTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void SortMatchesAStableSort(int shape)
    {
        var random = new Random(shape);
        // Nearly in order, in reverse (which takes the fallback), random, and all at one tick.
        List<(long Tick, int Index)> list = [.. Enumerable.Range(0, 2_000).Select(i => shape switch
        {
            0 => ((long)(i + random.Next(-3, 4)), i),
            1 => ((long)(2_000 - i) / 3, i),
            2 => ((long)random.Next(0, 50), i),
            _ => (7L, i),
        })];
        var expected = list.OrderBy(e => e.Tick).ToList();

        TickOrder.Sort(list, static e => e.Tick);

        Assert.AreSequenceEqual(expected, list);
    }

    [TestMethod]
    public void SortedReturnsTheListItselfWhenInOrder()
    {
        List<long> ordered = [1, 1, 2, 5];
        List<long> unordered = [3, 1, 2];

        Assert.AreSame(ordered, TickOrder.Sorted(ordered, static t => t));
        Assert.AreSequenceEqual([1L, 2, 3], TickOrder.Sorted(unordered, static t => t));
        Assert.AreSequenceEqual([3L, 1, 2], unordered);
    }

    [TestMethod]
    public void KeepFirstOfEachKeepsTheFirstOfAKeyAtATick()
    {
        List<(long Tick, int Key, string Name)> list =
        [
            (0, 1, "a"), (0, 2, "b"), (0, 1, "c"),
            (5, 1, "d"), (5, 1, "e"),
            (9, 2, "f"),
        ];

        TickOrder.KeepFirstOfEach(list, static e => e.Tick, static e => e.Key);

        Assert.AreSequenceEqual(["a", "b", "d", "f"], [.. list.Select(e => e.Name)]);
    }

    [TestMethod]
    [DataRow(1000, 2000)]
    [DataRow(2000, 1000)]
    public void MidiFretPlayedTwiceAtATickKeepsTheFirstLetGo(int secondOff, int firstOff)
    {
        // Two greens at tick 0 on different channels: the one let go first, the shorter, is kept.
        byte[] midi = new MidiBuilder().Track().Tempo(0, 120).Track("PART GUITAR")
            .Raw(0, 0x91, 96, 100).Raw(0, 0x92, 96, 100).Raw(secondOff, 0x92, 96, 0).Raw(firstOff, 0x91, 96, 0)
            .Build();

        var note = ChartReaderTests.Notes(ChartReader.Read(midi, "notes.mid"), ChartInstrument.Guitar).Single();

        Assert.AreEqual(1000L, note.Length);
    }

    [TestMethod]
    [DataRow("utf-8")]
    [DataRow("utf-8-bom")]
    [DataRow("utf-16")]
    [DataRow("utf-16BE")]
    [DataRow("utf-32")]
    public void ChartTextIsDecodedAsItsByteOrderMarkSays(string encoding)
    {
        const string Text = "[Song]\n{\n  Name = \"Café ♪\"\n}\n";
        byte[] bytes = encoding switch
        {
            "utf-8" => Encoding.UTF8.GetBytes(Text),
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Text)],
            _ => [.. Encoding.GetEncoding(encoding).GetPreamble(), .. Encoding.GetEncoding(encoding).GetBytes(Text)],
        };

        Assert.AreEqual(Text, ChartTextReader.Decode(bytes));
    }

    [TestMethod]
    public void ChartTextThatIsNotUtf8IsReplacedAsBefore()
    {
        byte[] bytes = [(byte)'a', 0xC3, (byte)'b', 0xE9];

        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Assert.AreEqual(reader.ReadToEnd(), ChartTextReader.Decode(bytes));
    }
}
