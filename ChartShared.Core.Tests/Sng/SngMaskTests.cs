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

        Assert.AreSequenceEqual(new byte[] { 0x51, 0x52, 0x53 }, data);
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
}
