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

    [TestMethod]
    public void EachDeviceTakesTheEndpointWithItsName()
    {
        var candidates = AudioDevices.Pair(
            [("speakers-id", "Speakers"), ("headphones-id", "Headphones")],
            [(2u, "Headphones"), (1u, "Speakers")]);

        Assert.HasCount(2, candidates);
        Assert.AreEqual(new OutputDeviceInfo("headphones-id", "Headphones"), candidates[0].Info);
        Assert.AreEqual(2u, candidates[0].SdlId);
        Assert.AreEqual(new OutputDeviceInfo("speakers-id", "Speakers"), candidates[1].Info);
        Assert.AreEqual(1u, candidates[1].SdlId);
    }

    [TestMethod]
    public void DevicesWithTheSameNameGetDifferentEndpoints()
    {
        var candidates = AudioDevices.Pair(
            [("first-id", "USB Headset"), ("second-id", "USB Headset")],
            [(1u, "USB Headset"), (2u, "USB Headset")]);

        Assert.HasCount(2, candidates);
        Assert.AreEqual("first-id", candidates[0].Info.Identifier);
        Assert.AreEqual("second-id", candidates[1].Info.Identifier);
    }

    [TestMethod]
    public void AnEndpointIsGivenToOneDeviceOnly()
    {
        var candidates = AudioDevices.Pair([("only-id", "Speakers")], [(1u, "Speakers"), (2u, "Speakers")]);

        Assert.HasCount(1, candidates);
        Assert.AreEqual(1u, candidates[0].SdlId);
    }

    [TestMethod]
    public void ADeviceWithoutAnEndpointOfItsNameIsLeftOut()
    {
        var candidates = AudioDevices.Pair([("speakers-id", "Speakers")], [(1u, "Speakers (Renamed)"), (2u, null)]);

        Assert.IsEmpty(candidates);
    }

    [TestMethod]
    public void NamesArePairedWithoutCase()
    {
        var candidates = AudioDevices.Pair([("speakers-id", "Speakers")], [(1u, "SPEAKERS")]);

        Assert.HasCount(1, candidates);
    }
}
