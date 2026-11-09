using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

/// <summary>
/// <see cref="FramePacer"/>: the screen shows frames evenly, but they're got round to unevenly; each is drawn for the screen's
/// beat, the same moment for everything in a frame, a missed frame skipped over, and the beat begun again after a long while.
/// </summary>
[TestClass]
public sealed class FramePacerTests
{
    // A clock counting microseconds, and a 75 Hz screen.
    private const long Frequency = 1_000_000;
    private const double Beat = 1.0 / 75;

    [TestMethod]
    public void FramesGotRoundToUnevenlyAreDrawnForAnEvenBeat()
    {
        var pacer = new FramePacer();
        var random = new Random(3);

        // Each frame asked up to 3 ms after the screen's beat, a fifth of a frame, as unevenly as a busy program: drawn for
        // the beat, each comes within 2% of a beat after the last.
        var moments = new List<double>();
        for (int frame = 0; frame < 600; frame++)
            moments.Add(Seconds(pacer.Next(At((frame * Beat) + (random.NextDouble() * 0.003)), Frequency)));

        for (int i = 100; i < moments.Count; i++)
        {
            double step = moments[i] - moments[i - 1];
            Assert.AreEqual(Beat, step, Beat * 0.02, $"Frame {i} came {step * 1000:F3} ms after the last.");
        }
    }

    [TestMethod]
    public void EverythingDrawnInAFrameIsDrawnForOneMoment()
    {
        var pacer = new FramePacer();
        for (int frame = 0; frame < 50; frame++)
        {
            double start = (frame * Beat) + 0.001;
            long first = pacer.Next(At(start), Frequency);
            Assert.AreEqual(first, pacer.Next(At(start + 0.001), Frequency), $"Frame {frame}'s second drawing.");
            Assert.AreEqual(first, pacer.Next(At(start + 0.0025), Frequency), $"Frame {frame}'s third drawing.");
        }
    }

    [TestMethod]
    public void AMissedFrameIsSkippedOver()
    {
        var pacer = new FramePacer();
        long last = 0;
        for (int frame = 0; frame < 40; frame++)
            last = pacer.Next(At(frame * Beat), Frequency);

        // Frame 40 is missed: 41 is two beats on.
        long next = pacer.Next(At(41 * Beat), Frequency);
        Assert.AreEqual(2 * Beat, Seconds(next) - Seconds(last), 1e-5);
    }

    [TestMethod]
    public void ThoughItKeepsUpWithOnlyEveryOtherFrameAtTimesTheBeatIsTheScreens()
    {
        var pacer = new FramePacer();

        // Every other frame missed, but for one in four: the commonest time between them is two beats.
        double at = 0;
        for (int frame = 0; frame < 40; frame++)
        {
            pacer.Next(At(at), Frequency);
            at += frame % 4 == 0 ? Beat : 2 * Beat;
        }

        Assert.AreEqual(Beat, pacer.FrameSeconds, Beat * 0.01);
    }

    [TestMethod]
    public void ItFollowsAScreenSlightlyFasterThanTheBeatItLearned()
    {
        var pacer = new FramePacer();
        for (int frame = 0; frame < 20; frame++)
            pacer.Next(At(frame * Beat), Frequency);

        // From here the screen runs 0.5% faster: the beat follows it, and the frames keep to it.
        double fast = Beat * 0.995;
        double start = 20 * Beat;
        long drawn = 0;
        double asked = 0;
        for (int frame = 0; frame < 2000; frame++)
        {
            asked = start + (frame * fast);
            drawn = pacer.Next(At(asked), Frequency);
        }

        Assert.AreEqual(fast, pacer.FrameSeconds, fast * 0.001);
        Assert.AreEqual(asked, Seconds(drawn), 0.001);
    }

    [TestMethod]
    public void AReadBetweenFramesIsItsOwnMomentAndTheNextFrameKeepsTheBeat()
    {
        // As a pointer moving the view reads the clock between two frames: that read is its own moment, and the next frame
        // is still a beat after the last, not taken for the same frame as the read.
        var pacer = new FramePacer();
        long last = 0;
        for (int frame = 0; frame < 40; frame++)
            last = pacer.Next(At(frame * Beat), Frequency);

        double between = (39 * Beat) + (Beat * 0.6);
        Assert.AreEqual(At(between), pacer.Next(At(between), Frequency));
        long next = pacer.Next(At(40 * Beat), Frequency);
        Assert.AreEqual(Beat, Seconds(next) - Seconds(last), 1e-5);
    }

    [TestMethod]
    public void AfterALongWhileItStartsAgainFromWhenItsAsked()
    {
        var pacer = new FramePacer();
        for (int frame = 0; frame < 20; frame++)
            pacer.Next(At(frame * Beat), Frequency);

        double later = (20 * Beat) + 2.0037;
        Assert.AreEqual(At(later), pacer.Next(At(later), Frequency));
    }

    // A moment in seconds as the clock's timestamp, from a start well past zero; and back.
    private static long At(double seconds) => (long)Math.Round((1000 + seconds) * Frequency);

    private static double Seconds(long timestamp) => ((double)timestamp / Frequency) - 1000;
}
