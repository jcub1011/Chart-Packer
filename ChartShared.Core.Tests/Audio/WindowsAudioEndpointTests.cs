using System.Runtime.Versioning;
using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
[SupportedOSPlatform("windows")]
[OSCondition(OperatingSystems.Windows)]
public sealed class WindowsAudioEndpointTests
{
    [TestMethod]
    public void TheDefaultOutputsIdIsAnEndpointIdOrNone()
    {
        // Null on a machine with no output device, as a build server may be.
        string? id = WindowsAudioEndpoint.DefaultRenderId();

        if (id is not null)
            Assert.StartsWith("{0.0.0.", id, $"'{id}' isn't a render endpoint's ID.");
        Assert.AreEqual(id, WindowsAudioEndpoint.DefaultRenderId());
    }

    [TestMethod]
    public void TheDefaultOutputsLatencyIsWithinASecondOrNone()
    {
        // Null on a machine with no output device, as a build server may be.
        var latency = WindowsAudioEndpoint.DefaultRenderLatency();

        if (latency is { } found)
        {
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, found);
            Assert.IsLessThan(TimeSpan.FromSeconds(1), found);
        }
    }
}
