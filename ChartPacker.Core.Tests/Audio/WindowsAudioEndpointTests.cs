using ChartPacker.Audio.Playback;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class WindowsAudioEndpointTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void TheDefaultOutputsIdIsAnEndpointIdOrNone()
    {
        // Null on a machine with no output device, as a build server may be.
        string? id = WindowsAudioEndpoint.DefaultRenderId();

        if (id is not null)
            Assert.StartsWith("{0.0.0.", id, $"'{id}' isn't a render endpoint's ID.");
        Assert.AreEqual(id, WindowsAudioEndpoint.DefaultRenderId());
    }
}
