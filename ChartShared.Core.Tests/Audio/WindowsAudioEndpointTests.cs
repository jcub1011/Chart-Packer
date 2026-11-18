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

    [TestMethod]
    public void TheEnumeratedEndpointsAreWellFormedWhenThereAreAny()
    {
        var endpoints = WindowsAudioEndpoint.RenderEndpoints();

        foreach (var (id, name) in endpoints)
        {
            Assert.StartsWith("{0.0.0.", id, $"'{id}' isn't a render endpoint's ID.");
            Assert.IsNotEmpty(name);
        }

        Assert.AreEqual(endpoints.Count, endpoints.Select(endpoint => endpoint.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public void AKnownEndpointIdReadsBackItsId()
    {
        var endpoints = WindowsAudioEndpoint.RenderEndpoints();
        if (endpoints.Count == 0)
            return; // A build server may have no output device.

        var (read, latency) = WindowsAudioEndpoint.RenderIdAndLatency(endpoints[0].Id);

        Assert.AreEqual(endpoints[0].Id, read, StringComparer.OrdinalIgnoreCase);
        if (latency is { } found)
        {
            Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, found);
            Assert.IsLessThan(TimeSpan.FromSeconds(1), found);
        }
    }

    [TestMethod]
    public void AMissingEndpointIdReadsAsNone()
    {
        var (read, latency) = WindowsAudioEndpoint.RenderIdAndLatency("{00000000-0000-0000-0000-000000000000}");

        Assert.IsNull(read);
        Assert.IsNull(latency);
    }

    [TestMethod]
    public void ANullEndpointIdReadsTheDefaultEndpoint()
    {
        Assert.AreEqual(WindowsAudioEndpoint.DefaultRenderIdAndLatency(), WindowsAudioEndpoint.RenderIdAndLatency(null));
        Assert.AreEqual(WindowsAudioEndpoint.DefaultRenderIdAndLatency(), WindowsAudioEndpoint.RenderIdAndLatency(""));
    }
}
