using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class AudioDevicesTests
{
    private static readonly OutputDeviceInfo Speakers = new("speakers-id", "Speakers (High Definition Audio Device)");
    private static readonly OutputDeviceInfo Headphones = new("headphones-id", "Headphones (USB Audio Device)");

    [TestMethod]
    public void NothingSavedIsTheDefaultDevice()
    {
        Assert.IsNull(AudioDevices.Resolve(null, [Speakers]));
        Assert.IsNull(AudioDevices.Resolve("", [Speakers]));
        Assert.IsNull(AudioDevices.Resolve("   ", [Speakers]));
    }

    [TestMethod]
    public void ASavedDeviceThatIsConnectedIsFound()
    {
        var found = AudioDevices.Resolve("headphones-id", [Speakers, Headphones]);

        Assert.AreEqual(Headphones, found);
    }

    [TestMethod]
    public void ASavedDeviceThatIsntConnectedIsNone()
    {
        Assert.IsNull(AudioDevices.Resolve("headphones-id", [Speakers]));
        Assert.IsNull(AudioDevices.Resolve("headphones-id", []));
    }

    [TestMethod]
    public void TheIdentifierIsMatchedWithoutCase()
    {
        var found = AudioDevices.Resolve("HEADPHONES-ID", [Headphones]);

        Assert.AreEqual(Headphones, found);
    }

    [TestMethod]
    public void TheFirstOfTwoDevicesWithTheSameIdentifierIsFound()
    {
        var first = new OutputDeviceInfo("id", "First");
        var second = new OutputDeviceInfo("id", "Second");

        Assert.AreEqual(first, AudioDevices.Resolve("id", [first, second]));
    }
}
