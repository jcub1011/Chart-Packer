using ChartShared.Core.Sng;

namespace ChartShared.Core.Tests.Sng;

[TestClass]
public sealed class SngMaskTests
{
    private static readonly byte[] ExampleMask = [.. Enumerable.Range(0x10, 16).Select(b => (byte)b)];

    [TestMethod]
    public void WorkedExampleMasksAbc()
    {
        byte[] data = "ABC"u8.ToArray();

        SngMask.Apply(data, ExampleMask);

        Assert.AreSequenceEqual("QRS"u8.ToArray(), data);
    }

    [TestMethod]
    public void MatchesSpecFormula()
    {
        byte[] mask = SngMask.Generate();
        byte[] data = new byte[1000];
        new Random(7).NextBytes(data);
        byte[] expected = [.. data.Select((b, i) => (byte)(b ^ mask[i % 16] ^ (i & 0xFF)))];

        SngMask.Apply(data, mask);

        Assert.AreSequenceEqual(expected, data);
    }

    [TestMethod]
    public void ChunksAtOddOffsetsMatchOnePass()
    {
        byte[] mask = SngMask.Generate();
        byte[] whole = new byte[3001];
        new Random(11).NextBytes(whole);
        byte[] chunked = (byte[])whole.Clone();

        SngMask.Apply(whole, mask);
        int pos = 0;
        foreach (int size in new[] { 1, 7, 255, 256, 257, 1000, 1225 })
        {
            SngMask.Apply(chunked.AsSpan(pos, size), mask, pos);
            pos += size;
        }

        Assert.AreEqual(whole.Length, pos);
        Assert.AreSequenceEqual(whole, chunked);
    }

    [TestMethod]
    public void MaskingTwiceRestoresTheData()
    {
        byte[] mask = SngMask.Generate();
        byte[] original = new byte[600];
        new Random(3).NextBytes(original);
        byte[] data = (byte[])original.Clone();

        SngMask.Apply(data, mask, 12345);
        SngMask.Apply(data, mask, 12345);

        Assert.AreSequenceEqual(original, data);
    }

    [TestMethod]
    public void GenerateReturnsSixteenBytes()
    {
        Assert.HasCount(SngMask.MaskSize, SngMask.Generate());
    }

    [TestMethod]
    public void EveryLengthAndStartMatchesTheSpecFormula()
    {
        // Lengths either side of a vector's width and of the key's 256 bytes, from starts all round the key.
        byte[] mask = [0x5A, 0x01, 0xFF, 0x10, 0x80, 0x7E, 0x33, 0xC4, 0x09, 0xE2, 0x48, 0x91, 0x6D, 0xB7, 0x2C, 0xF0];
        var random = new Random(7);
        foreach (int length in (int[])[0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 255, 256, 257, 1000])
        {
            foreach (long start in (long[])[0, 1, 15, 16, 200, 255, 256, 300, 70_000_000_001])
            {
                byte[] data = new byte[length];
                random.NextBytes(data);
                byte[] expected = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    long at = start + i;
                    expected[i] = (byte)(data[i] ^ mask[at % 16] ^ (byte)(at & 0xFF));
                }

                SngMask.Apply(data, mask, start);

                CollectionAssert.AreEqual(expected, data, $"length {length} from {start}");
            }
        }
    }
}
