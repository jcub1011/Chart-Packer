using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Scanning;

[TestClass]
public sealed class PooledReadStreamTests
{
    [TestMethod]
    public void ReadsWhatTheStreamUnderItHoldsWhereverItSeeks()
    {
        var random = new Random(11);
        byte[] data = new byte[10_000];
        random.NextBytes(data);
        using var stream = new PooledReadStream(new MemoryStream(data), 256);

        Assert.AreEqual(data.Length, stream.Length);
        for (int round = 0; round < 500; round++)
        {
            long at = random.Next(0, data.Length + 10);
            Assert.AreEqual(at, random.Next(2) == 0 ? stream.Seek(at, SeekOrigin.Begin) : stream.Seek(at - stream.Position, SeekOrigin.Current));

            // Small reads come from the buffer; one as large as it goes straight to the stream.
            byte[] read = new byte[random.Next(4) == 0 ? 700 : random.Next(1, 40)];
            int count = stream.Read(read);

            Assert.IsTrue(count > 0 || at >= data.Length, $"round {round}: nothing read at {at}");
            Assert.AreSequenceEqual(data.AsSpan((int)Math.Min(at, data.Length), count).ToArray(), read.AsSpan(0, count).ToArray(), $"round {round}");
            Assert.AreEqual(at + count, stream.Position);
        }
    }

    [TestMethod]
    public void ReadsToTheEndInPieces()
    {
        byte[] data = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        using var stream = new PooledReadStream(new MemoryStream(data), 64);
        using var copy = new MemoryStream();

        stream.CopyTo(copy, 10);

        Assert.AreSequenceEqual(data, copy.ToArray());
        Assert.AreEqual(0, stream.Read(new byte[5]));
    }

    [TestMethod]
    public void SeekingBeforeTheStartThrowsAsAFileStreamDoes()
    {
        using var stream = new PooledReadStream(new MemoryStream(new byte[100]), 16);
        stream.Position = 10;

        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-11, SeekOrigin.Current));
        Assert.Throws<IOException>(() => stream.Seek(-101, SeekOrigin.End));
        Assert.AreEqual(10, stream.Position);
    }

    [TestMethod]
    public void DisposingItDisposesTheStreamUnderIt()
    {
        var inner = new MemoryStream([1, 2, 3]);
        var stream = new PooledReadStream(inner, 16);

        stream.Dispose();

        Assert.IsFalse(inner.CanRead);
        Assert.IsFalse(stream.CanRead);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }
}
