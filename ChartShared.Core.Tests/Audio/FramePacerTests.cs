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

    [TestMethod]
    [DataRow(75, 4.5)]
    [DataRow(60, 5.5)]
    public void AViewReadLateInEveryFrameWhileItLearnsIsStillTheSameFrame(int hertz, double lateMilliseconds)
    {
        // The views drawn in a frame's first moments read the clock, then one drawn after them, as the timeline's playhead,
        // reads it some milliseconds on: every frame's time split in two. The beat is still the screen's, and the late read
        // the same frame as the others.
        double beat = 1.0 / hertz;
        var pacer = new FramePacer();
        for (int frame = 0; frame < 60; frame++)
        {
            double start = frame * beat;
            long first = pacer.Next(At(start), Frequency);
            long late = pacer.Next(At(start + (lateMilliseconds / 1000)), Frequency);
            if (frame >= 40)
                Assert.AreEqual(first, late, $"Frame {frame}'s late read.");
        }

        Assert.AreEqual(beat, pacer.FrameSeconds, beat * 0.01);
    }

    [TestMethod]
    public void OneLateReadWhileItLearnsDoesNotSpoilTheBeat()
    {
        var pacer = new FramePacer();
        for (int frame = 0; frame < 30; frame++)
        {
            pacer.Next(At(frame * Beat), Frequency);
            if (frame == 5)
                pacer.Next(At((frame * Beat) + 0.005), Frequency);
        }

        Assert.AreEqual(Beat, pacer.FrameSeconds, Beat * 0.01);
    }

    [TestMethod]
    public void AFasterScreenIsLearnedAgain()
    {
        // From 75 Hz to 144 Hz, as the window is moved to another screen: kept, the beat would have every other frame taken for
        // the one before it, and drawn again.
        var pacer = new FramePacer();
        for (int frame = 0; frame < 40; frame++)
            pacer.Next(At(frame * Beat), Frequency);

        AssertLearnsAgain(pacer, from: 40 * Beat, beat: 1.0 / 144);
    }

    [TestMethod]
    public void ASlowerScreenIsLearnedAgain()
    {
        var pacer = new FramePacer();
        const double Fast = 1.0 / 144;
        for (int frame = 0; frame < 60; frame++)
            pacer.Next(At(frame * Fast), Frequency);

        AssertLearnsAgain(pacer, from: 60 * Fast, beat: 1.0 / 60);
    }

    /// <summary>Asserts that frames <paramref name="beat"/> apart from <paramref name="from"/> are soon each drawn a beat after the last.</summary>
    private static void AssertLearnsAgain(FramePacer pacer, double from, double beat)
    {
        var moments = new List<double>();
        for (int frame = 0; frame < 200; frame++)
            moments.Add(Seconds(pacer.Next(At(from + (frame * beat)), Frequency)));

        Assert.AreEqual(beat, pacer.FrameSeconds, beat * 0.01);
        for (int i = 100; i < moments.Count; i++)
        {
            double step = moments[i] - moments[i - 1];
            Assert.AreEqual(beat, step, beat * 0.02, $"Frame {i} came {step * 1000:F3} ms after the last.");
        }
    }

    // A moment in seconds as the clock's timestamp, from a start well past zero; and back.
    private static long At(double seconds) => (long)Math.Round((1000 + seconds) * Frequency);

    private static double Seconds(long timestamp) => ((double)timestamp / Frequency) - 1000;
}
