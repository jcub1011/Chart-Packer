using ChartPacker.Core.Charting;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class RawEventTests
{
    [TestMethod]
    public void EventsWithTheSameTickAndBytesAreEqual()
    {
        var a = new RawEvent(480, [0x90, 40, 100]);
        var b = new RawEvent(480, [0x90, 40, 100]);

        Assert.AreEqual(a, b);
        Assert.IsTrue(a == b);
        Assert.IsFalse(a != b);
        Assert.IsTrue(a.Equals((object)b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void EventsWithOtherBytesOrTicksAreNotEqual()
    {
        var a = new RawEvent(480, [0x90, 40, 100]);

        Assert.AreNotEqual(a, new RawEvent(480, [0x90, 40, 101]));
        Assert.AreNotEqual(a, new RawEvent(480, [0x90, 40]));
        Assert.AreNotEqual(a, new RawEvent(481, [0x90, 40, 100]));
        Assert.IsTrue(a != new RawEvent(0, [0x90, 40, 100]));
    }

    [TestMethod]
    public void TheBytesReadBackAsGiven()
    {
        var e = new RawEvent(0, [0xF0, 1, 0xF7]);

        Assert.AreSequenceEqual(new byte[] { 0xF0, 1, 0xF7 }, e.Bytes.ToArray());
        Assert.AreEqual((byte)0xF0, e.Status);
    }

    [TestMethod]
    public void DefaultEventsCompareWithoutThrowing()
    {
        Assert.AreEqual(default, default(RawEvent));
        Assert.AreNotEqual(default, new RawEvent(0, [0x90, 40, 100]));
        _ = default(RawEvent).GetHashCode();
    }

    [TestMethod]
    public void AnEventNeedsAStatusByte()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RawEvent(0, []));
    }
}
