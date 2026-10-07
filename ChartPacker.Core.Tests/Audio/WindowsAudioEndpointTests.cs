using ChartPacker.Audio.Playback;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class WindowsAudioEndpointTests
{
    [TestMethod]
    public void TheDefaultOutputsIdIsAnEndpointIdOrNone()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows only.");

        // Null on a machine with no output device, as a build server may be.
        string? id = WindowsAudioEndpoint.DefaultRenderId();

        if (id is not null)
            StringAssert.StartsWith(id, "{0.0.0.", $"'{id}' isn't a render endpoint's ID.");
        Assert.AreEqual(id, WindowsAudioEndpoint.DefaultRenderId());
    }
}
