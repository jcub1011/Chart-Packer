using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class SynthTests
{
    private const int Rate = 48000;

    [TestMethod]
    [DataRow(1000.0)]
    [DataRow(2000.0)]
    public void ABeepIsItsToneForItsLengthWithShortFades(double hz)
    {
        var beep = Synth.Beep(hz, 30, 2, Rate);

        Assert.AreEqual(Rate * 30 / 1000, beep.Frames);
        Assert.AreEqual(TimeSpan.FromMilliseconds(30), beep.Duration);
        Assert.AreEqual(hz, Frequency(beep), hz / 30);
        Assert.AreEqual(0f, beep.Samples[0]);
        Assert.AreEqual(0f, beep.Samples[^1], 1e-6f);
        Assert.AreEqual(Synth.Peak, Loudest(beep), 0.01f);

        // Faded in over 2 ms: quieter than full there, full from there on.
        int fade = Rate * 2 / 1000;
        Assert.IsLessThan(Synth.Peak * 0.5f, Loudest(beep.Samples[..(fade / 4)]));
        Assert.AreEqual(Synth.Peak, Loudest(beep.Samples[fade..(2 * fade)]), 0.01f);
    }

    [TestMethod]
    public void TheMeasureClickIsLouderAndHigherThanTheBeatClick()
    {
        var measure = Synth.Click(accented: true, Rate);
        var beat = Synth.Click(accented: false, Rate);

        Assert.AreEqual(Synth.Peak, Loudest(measure), 1e-6f);
        Assert.IsLessThan(Loudest(measure), Loudest(beat));
        Assert.IsGreaterThan(Frequency(beat), Frequency(measure));
        Assert.AreEqual(0f, measure.Samples[0], "A click starts at nothing, so its start is sharp.");
        Assert.IsLessThanOrEqualTo(0.030, measure.Duration.TotalSeconds);
    }

    [TestMethod]
    public void EveryHitSoundIsAtThePeakAndTheSameEachTime()
    {
        foreach (var kind in Enum.GetValues<SynthHit>())
        {
            var hit = Synth.Hit(kind, Rate);

            Assert.AreEqual(Synth.Peak, Loudest(hit), 1e-6f, $"{kind}");
            Assert.IsLessThan(0.5, hit.Duration.TotalSeconds, $"{kind}");
            Assert.AreSequenceEqual(hit.Samples.ToArray(), Synth.Hit(kind, Rate).Samples.ToArray(), $"{kind}");
        }
    }

    [TestMethod]
    public void ASoundMadeAtAnotherRateLastsAsLong()
    {
        var made = Synth.Beep(1000, 30, 2, Rate / 2);

        Assert.AreEqual(Rate / 2, made.SampleRate);
        Assert.AreEqual(TimeSpan.FromMilliseconds(30), made.Duration);
        Assert.AreEqual(1000, Frequency(made), 1000.0 / 30);
    }

    [TestMethod]
    public void TheMetronomeClicksEachBeatInAudioTimeAndAccentsEachMeasure()
    {
        // 120 BPM in 4/4 to tick 1920, then 3/4 at 60 BPM; the chart plays 100 ms later than its ticks.
        var map = new TempoMap(480, [new TempoChange(0, 500_000), new TempoChange(1920, 1_000_000)], [new TimeSignature(0, 4, 4), new TimeSignature(1920, 3, 4)]);
        var metronome = new MetronomeSchedule(map, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5.1), Rate);

        double[] seconds = [0, 0.5, 1, 1.5, 2, 3, 4, 5];
        bool[] measures = [true, false, false, false, true, false, false, true];
        Assert.AreEqual(seconds.Length, metronome.Count);
        for (int i = 0; i < seconds.Length; i++)
        {
            Assert.AreEqual((long)Math.Round((seconds[i] + 0.1) * Rate), metronome.FrameAt(i), $"Click {i}");
            Assert.AreEqual(measures[i], metronome.IsMeasureAt(i), $"Click {i}");
        }

        var found = new List<ScheduledSound>();
        metronome.SoundsBetween(metronome.FrameAt(1), metronome.FrameAt(4), found);
        Assert.AreSequenceEqual(new[] { metronome.FrameAt(1), metronome.FrameAt(2), metronome.FrameAt(3) }, [.. found.Select(s => s.SourceFrame)], "From the first frame, up to but not including the last.");
        Assert.AreSame(found[0].Sound, found[1].Sound, "Every beat has the same click.");
        Assert.AreNotSame(found[0].Sound, Clicks(metronome, 4).Single().Sound, "A measure's click isn't a beat's.");
        Assert.AreSame(Clicks(metronome, 0).Single().Sound, Clicks(metronome, 4).Single().Sound);
    }

    [TestMethod]
    public void TheMetronomeLeavesOutBeatsBeforeTheAudioStartsAndAfterItEnds()
    {
        // The chart plays 0.7 s earlier than its ticks: its first two beats are before the audio starts, and its beat
        // at 2.5 s of chart time is at 1.8 s of the audio, the last before it ends at 2 s.
        var map = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        var metronome = new MetronomeSchedule(map, TimeSpan.FromMilliseconds(-700), TimeSpan.FromSeconds(2), Rate);

        Assert.AreEqual(4, metronome.Count);
        Assert.AreEqual((long)Math.Round(0.3 * Rate), metronome.FrameAt(0));
        Assert.AreEqual((long)Math.Round(1.8 * Rate), metronome.FrameAt(3));
    }

    private static IEnumerable<ScheduledSound> Clicks(MetronomeSchedule metronome, int index)
    {
        var found = new List<ScheduledSound>();
        metronome.SoundsBetween(metronome.FrameAt(index), metronome.FrameAt(index) + 1, found);
        return found;
    }

    private static float Loudest(SynthSound sound) => Loudest(sound.Samples);

    private static float Loudest(ReadOnlySpan<float> samples)
    {
        float loudest = 0;
        foreach (float sample in samples)
            loudest = Math.Max(loudest, Math.Abs(sample));
        return loudest;
    }

    /// <summary>The sound's frequency, from how often it crosses zero.</summary>
    private static double Frequency(SynthSound sound)
    {
        var samples = sound.Samples;
        int crossings = 0;
        for (int n = 1; n < samples.Length; n++)
        {
            if ((samples[n - 1] < 0) != (samples[n] < 0))
                crossings++;
        }

        return crossings / 2.0 / sound.Duration.TotalSeconds;
    }
}
