using ChartShared.Core.Charting;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class PassthroughTrackTests
{
    private static byte[] Track(byte last = 0) => [.. "MTrk"u8, 0, 0, 0, 4, 0, 0xFF, 0x2F, last];

    [TestMethod]
    public void TracksWithTheSameBytesAndNameAreEqual()
    {
        var a = new PassthroughTrack(Track(), "PART VOCALS");
        var b = new PassthroughTrack(Track(), "PART VOCALS");

        Assert.AreEqual(a, b);
        Assert.IsTrue(a.Equals((object)b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void TracksWithOtherBytesOrNamesAreNotEqual()
    {
        var a = new PassthroughTrack(Track(), "PART VOCALS");

        Assert.AreNotEqual(a, new PassthroughTrack(Track(1), "PART VOCALS"));
        Assert.AreNotEqual(a, new PassthroughTrack(Track(), "HARM1"));
        Assert.AreNotEqual(a, new PassthroughTrack(Track(), null));
        Assert.IsFalse(a.Equals(null));
    }

    [TestMethod]
    public void TheChunkTypeSaysWhetherItIsATrack()
    {
        var track = new PassthroughTrack(Track(), "VENUE");
        var other = new PassthroughTrack([.. "XFIH"u8, 0, 0, 0, 2, 1, 2], null);

        Assert.IsTrue(track.IsTrack);
        Assert.AreEqual("MTrk", track.ChunkType);
        Assert.IsFalse(other.IsTrack);
        Assert.AreEqual("XFIH", other.ChunkType);
        Assert.AreSequenceEqual(Track(), track.Chunk.ToArray());
    }

    [TestMethod]
    public void AChunkNeedsItsHeader()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PassthroughTrack([.. "MTrk"u8, 0, 0], null));
    }
}
