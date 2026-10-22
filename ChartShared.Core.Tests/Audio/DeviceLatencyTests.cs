using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class DeviceLatencyTests
{
    [TestMethod]
    public void TheBufferIsItsFramesAtTheDevicesRate()
    {
        Assert.AreEqual(10, DeviceLatency.Milliseconds(480, 48000, null));
        Assert.AreEqual(23, DeviceLatency.Milliseconds(1024, 44100, null));
    }

    [TestMethod]
    public void TheOsLatencyIsAddedToTheBuffer()
    {
        Assert.AreEqual(30, DeviceLatency.Milliseconds(480, 48000, TimeSpan.FromMilliseconds(20)));
        Assert.AreEqual(20, DeviceLatency.Milliseconds(0, 0, TimeSpan.FromMilliseconds(20)));
    }

    [TestMethod]
    public void NothingKnownIsNoLatency()
    {
        Assert.IsNull(DeviceLatency.Milliseconds(0, 48000, null));
        Assert.IsNull(DeviceLatency.Milliseconds(480, 0, null));
        Assert.IsNull(DeviceLatency.Milliseconds(-480, -48000, TimeSpan.Zero));
        Assert.IsNull(DeviceLatency.Milliseconds(480, -48000, TimeSpan.FromMilliseconds(-5)));
    }

    [TestMethod]
    public void ItIsKeptWithinFiveHundredMilliseconds()
    {
        Assert.AreEqual(DeviceLatency.MaxMilliseconds, DeviceLatency.Milliseconds(48000, 48000, TimeSpan.FromSeconds(2)));
    }
}
