using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class LatencyCompensationTests
{
    /// <summary>
    /// AC C10.2: changing the calibrated latency moves what's drawn against the audio by exactly that amount times the
    /// speed, and doesn't move the audio.
    /// </summary>
    [TestMethod]
    [DataRow(0.5)]
    [DataRow(1.0)]
    [DataRow(1.5)]
    public void ALatencyChangeMovesWhatsDrawnByItTimesTheSpeed(double speed)
    {
        var position = TimeSpan.FromSeconds(42.125);

        var before = LatencyCompensation.Drawn(position, 40, speed);
        var after = LatencyCompensation.Drawn(position, 65, speed);

        Assert.AreEqual(-25 * speed, (after - before).TotalMilliseconds, 1e-6);
        Assert.AreEqual(position.TotalMilliseconds - (40 * speed), before.TotalMilliseconds, 1e-6);
    }

    [TestMethod]
    public void ANegativeLatencyDrawsLater()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(1030), LatencyCompensation.Drawn(TimeSpan.FromSeconds(1), -30, 1));
    }

    [TestMethod]
    public void ASpeedThatIsNoSpeedCountsAsOne()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(950), LatencyCompensation.Drawn(TimeSpan.FromSeconds(1), 50, 0));
        Assert.AreEqual(TimeSpan.FromMilliseconds(950), LatencyCompensation.Drawn(TimeSpan.FromSeconds(1), 50, double.NaN));
    }
}
