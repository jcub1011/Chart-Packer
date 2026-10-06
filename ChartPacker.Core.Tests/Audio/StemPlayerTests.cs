using System.Diagnostics;
using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Dsp;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Synthesis;
using ChartPacker.Core.Charts;
using static ChartPacker.Core.Tests.Audio.TestAudio;

namespace ChartPacker.Core.Tests.Audio;

/// <summary><see cref="StemPlayer"/> against an output that only drains when a test says it has played.</summary>
[TestClass]
public sealed class StemPlayerTests
{
    private const int Rate = 48000;
    private const int QueueTarget = Rate * StemPlayer.QueueMilliseconds / 1000;

    // Stretched, Position is where the grains played are, which the stretcher takes up to 12 ms from where the speed
    // says (TimeStretcher.PlayedPosition). On this player's stem, a steady ramp, every grain is taken nearly as far back as it
    // can be at any speed over 1 (about 10 ms at 1.5), as that lines up best with the grain before it carrying on.
    private const double Stretched = 0.013;

    [TestMethod]
    public void PlayingKeepsAboutTheQueueTargetQueued()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);

        player.Play();

        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        Thread.Sleep(50);
        Assert.IsLessThan(QueueTarget + (Rate / 20), output.QueuedFrames, $"{output.QueuedFrames} frames were queued.");
        Assert.IsTrue(output.IsPlaying);
    }

    [TestMethod]
    public void ThePositionIsWhatTheDeviceHasPlayed()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        // The player queues more as the device drains, but what's been heard is half a second.
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        player.Pause();
        Assert.AreEqual(0.5, player.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void PausingPausesTheDeviceAndKeepsThePosition()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        output.Drain(Rate / 4);

        player.Pause();
        var position = player.Position;
        Thread.Sleep(30);

        Assert.IsFalse(player.IsPlaying);
        Assert.IsFalse(output.IsPlaying);
        Assert.AreEqual(position, player.Position);

        player.Play();
        Assert.IsTrue(output.IsPlaying);
    }

    [TestMethod]
    public void SeekingDropsTheQueueAndCarriesOnFromTheTarget()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        player.Seek(TimeSpan.FromSeconds(4));

        Assert.AreEqual(4, player.Position.TotalSeconds, 0.001);
        WaitUntil(() => output.Clears > 0 && output.QueuedFrames >= QueueTarget);

        // What's queued after the seek starts at 4 s: the ramp's value there.
        Assert.AreEqual(4f / 10, output.FirstSampleAfterLastClear ?? float.NaN, 1e-4f);
        output.Drain(Rate / 10);
        player.Pause();
        Assert.AreEqual(4.1, player.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void SeekingWhilePausedMovesWithoutPlaying()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);

        player.Seek(TimeSpan.FromSeconds(2));
        WaitUntil(() => player.Mixer.PositionFrames == 2 * Rate);

        Assert.IsFalse(output.IsPlaying);
        Assert.AreEqual(2, player.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void TheEndStopsPlaybackOnceAndPlayStartsOver()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 0.1);
        int stops = 0;
        player.Stopped += (_, _) => Interlocked.Increment(ref stops);

        player.Play();
        WaitUntil(() => player.Mixer.IsEnded);
        output.Drain(output.QueuedFrames);
        WaitUntil(() => player.IsEnded);

        Thread.Sleep(30);
        Assert.AreEqual(1, stops);
        Assert.IsFalse(player.IsPlaying);
        Assert.IsNull(player.Error);
        Assert.AreEqual(0.1, player.Length.TotalSeconds, 0.001);

        player.Play();
        WaitUntil(() => output.QueuedFrames > 0);
        Assert.AreEqual(0f, output.FirstSampleAfterLastClear ?? float.NaN, 1e-6f);
        Assert.IsFalse(player.IsEnded);
    }

    [TestMethod]
    public void AMinimumLengthPlaysOnPastTheAudioInSilence()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 0.1);
        player.MinimumLength = TimeSpan.FromSeconds(0.3);

        player.Play();
        PlayFor(output, Rate / 5);
        Assert.IsTrue(player.IsPlaying);
        Assert.AreEqual(0.2, player.Position.TotalSeconds, 0.001);

        WaitUntil(() => player.Mixer.IsEnded);
        output.Drain(output.QueuedFrames);
        WaitUntil(() => player.IsEnded);
        Assert.AreEqual(0.3, player.Position.TotalSeconds, 0.001);
        Assert.AreEqual(0.1, player.Length.TotalSeconds, 0.001);
        Assert.AreEqual(0.3, player.MinimumLength.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void AFailingDeviceStopsPlaybackWithTheReason()
    {
        using var output = new FakeOutput { FailWrites = true };
        using var player = Player(output, seconds: 10);
        int stops = 0;
        player.Stopped += (_, _) => Interlocked.Increment(ref stops);

        player.Play();
        WaitUntil(() => Volatile.Read(ref stops) == 1);

        Assert.AreEqual("The device went away.", player.Error);
        Assert.IsFalse(player.IsPlaying);
        Assert.AreEqual(1, stops);
    }

    [TestMethod]
    public void DisposingStopsTheThreadAndClosesTheOutput()
    {
        var output = new FakeOutput();
        var player = Player(output, seconds: 10);
        player.Play();
        WaitUntil(() => output.QueuedFrames > 0);

        player.Dispose();

        Assert.IsTrue(output.IsDisposed);
        player.Play(); // Ignored once disposed.
    }

    [TestMethod]
    public void TheVolumeIsTheOutputs()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 1);

        player.Volume = 0.25f;

        Assert.AreEqual(0.25f, output.Volume);
    }

    [TestMethod]
    public void TheSpeedIsTheOutputs()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 1);

        player.Speed = 2.5f;

        Assert.AreEqual(2.5f, output.Speed);
    }

    [TestMethod]
    public void AFasterSpeedQueuesAsMuchMoreAsPlaysMeanwhile()
    {
        using var output = new FakeOutput { Speed = 4 };
        using var player = Player(output, seconds: 10);

        player.Play();

        // The device plays the queue four times as fast, so four times the frames last QueueMilliseconds.
        WaitUntil(() => output.QueuedFrames >= QueueTarget * 4);
        Thread.Sleep(50);
        Assert.IsLessThan((QueueTarget * 4) + (Rate / 20), output.QueuedFrames, $"{output.QueuedFrames} frames were queued.");
    }

    [TestMethod]
    public void ASlowSpeedQueuesNoMoreThanPlaysInTheQueueTime()
    {
        using var output = new FakeOutput { Speed = 0.01f };
        using var player = Player(output, seconds: 10);
        int target = QueueTarget / 100;

        player.Play();

        // Less than one ordinary 20 ms chunk, which would take two seconds to play at this speed.
        WaitUntil(() => output.QueuedFrames > 0);
        Thread.Sleep(50);
        Assert.IsLessThanOrEqualTo(target, output.QueuedFrames, $"{output.QueuedFrames} frames were queued.");
        Assert.IsLessThanOrEqualTo(target, output.LargestWrite, $"{output.LargestWrite} frames were written at once.");
    }

    [TestMethod]
    public void WhatIsHeardLagsWhatIsQueued()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        var heard = new float[2048];

        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        Assert.IsFalse(player.TryReadHeard(heard, out _), "Nothing has been heard yet.");

        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        // Half a second has been heard: the window ends there, however much more is queued.
        Assert.IsTrue(player.TryReadHeard(heard, out _));
        Assert.AreEqual((float)((Rate / 2) - 1) / (Rate * 10), heard[^1], 1e-6f);
        Assert.AreEqual((float)((Rate / 2) - heard.Length) / (Rate * 10), heard[0], 1e-6f);
    }

    [TestMethod]
    public void NothingIsHeardWhilePausedOrJustAfterASeek()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        var heard = new float[2048];
        player.Play();
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        player.Pause();
        Assert.IsFalse(player.TryReadHeard(heard, out _));

        player.Play();
        player.Seek(TimeSpan.FromSeconds(4));
        WaitUntil(() => output.Clears > 0 && output.QueuedFrames >= QueueTarget);
        Assert.IsFalse(player.TryReadHeard(heard, out _), "Nothing has been heard since the seek.");

        output.Drain(heard.Length);
        Assert.IsTrue(player.TryReadHeard(heard, out _));
        Assert.AreEqual(4f / 10, heard[0], 1e-4f);
    }

    [TestMethod]
    public void NothingIsHeardWhileNotCapturing()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        var heard = new float[2048];
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        player.CapturesHeard = false;
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        Assert.IsFalse(player.TryReadHeard(heard, out _));

        player.CapturesHeard = true;
        Assert.IsFalse(player.TryReadHeard(heard, out _), "Nothing was kept while it was off.");

        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        // A second has been heard in all: the window ends there, from what was played after it was turned on.
        Assert.IsTrue(player.TryReadHeard(heard, out _));
        Assert.AreEqual((float)(Rate - 1) / (Rate * 10), heard[^1], 1e-6f);
        Assert.AreEqual((float)(Rate - heard.Length) / (Rate * 10), heard[0], 1e-6f);
    }

    [TestMethod]
    public void WithPitchCorrectionTheDevicePlaysAtItsOwnSpeed()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.PitchCorrection = true;
        player.Speed = 2;

        player.Play();

        // The mix is stretched before it's queued, so the queue plays as written and holds QueueMilliseconds of it.
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        Thread.Sleep(50);
        Assert.AreEqual(1f, output.Speed);
        Assert.IsLessThan(QueueTarget + (Rate / 20), output.QueuedFrames, $"{output.QueuedFrames} frames were queued.");
        player.TryReadHeard(new float[1], out double heardRate);
        Assert.AreEqual(Rate, heardRate);
    }

    [TestMethod]
    public void WithPitchCorrectionThePositionMovesAtTheSpeed()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.PitchCorrection = true;
        player.Speed = 2;
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        // Half a second of output is a second of the song at twice the speed.
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        player.Pause();
        Assert.AreEqual(1, player.Position.TotalSeconds, Stretched);
    }

    [TestMethod]
    public void SwitchingPitchCorrectionCarriesOnFromWhatIsHeard()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.Speed = 2;
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        PlayFor(output, Rate / 2);
        Assert.AreEqual(2f, output.Speed);
        player.TryReadHeard(new float[1], out double heardRate);
        Assert.AreEqual(2.0 * Rate, heardRate);

        int clears = output.Clears;
        player.PitchCorrection = true;

        // What was queued unstretched is dropped, and the song carries on, stretched, from where it was heard.
        Assert.AreEqual(0.5, player.Position.TotalSeconds, 0.001);
        WaitUntil(() => output.Clears > clears && output.QueuedFrames >= QueueTarget);
        Assert.AreEqual(1f, output.Speed);
        Assert.AreEqual(0.5f / 10, output.FirstSampleAfterLastClear ?? float.NaN, 1e-6f);
        player.TryReadHeard(new float[1], out heardRate);
        Assert.AreEqual(Rate, heardRate);
    }

    [TestMethod]
    [DataRow(1f)]
    [DataRow(2f)]
    public void WithPitchCorrectionASpeedChangeKeepsTheQueue(float from)
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.PitchCorrection = true;
        player.Speed = from;
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        PlayFor(output, Rate / 4);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        player.Speed = 1.5f;
        Assert.AreEqual(0.25 * from, player.Position.TotalSeconds, Stretched);

        // What was queued, and stretched at the old speed meanwhile, plays out; what follows is at the new speed.
        PlayFor(output, QueueTarget * 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        double before = player.Position.TotalSeconds;
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        player.Pause();

        Assert.AreEqual(before + 0.75, player.Position.TotalSeconds, Stretched);
        Assert.AreEqual(0, output.Clears);
        Assert.AreEqual(1f, output.Speed);
    }

    [TestMethod]
    public void WhileStretchedASwitchIsHeardOnceTheQueueHasPlayed()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.PitchCorrection = true;
        player.Speed = 0.1f;
        player.Play();
        PlayFor(output, Rate / 4);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        long muted = output.WrittenFrames;
        player.Mixer.Stems[0].IsMuted = true;
        PlayFor(output, QueueTarget + (Rate / 10));
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        // The stretcher's mix is mixed again: past the chunk being put together then, what was left of a grain step and
        // the step fading out of it, it's silent. Unmixed again, half a second of the old mix would follow at this speed.
        float[] written = output.WrittenLeft();
        int from = (int)muted + (4 * Rate / 50);
        Assert.IsGreaterThan(from, written.Length);
        for (int n = from; n < written.Length; n++)
            Assert.AreEqual(0f, written[n], $"Frame {n - muted} after the mute was heard.");
    }

    [TestMethod]
    public void WithoutPitchCorrectionASpeedChangeKeepsTheQueue()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        player.Speed = 1.5f;
        Thread.Sleep(30);

        Assert.AreEqual(0, output.Clears);
        Assert.AreEqual(1.5f, output.Speed);
    }

    [TestMethod]
    public void AtItsOwnSpeedPitchCorrectionPlaysTheMixAsItIs()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 10);
        var heard = new float[2048];
        player.PitchCorrection = true;

        player.Play();
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);

        Assert.AreEqual(0, output.Clears);
        Assert.IsTrue(player.TryReadHeard(heard, out _));
        for (int n = 0; n < heard.Length; n++)
            Assert.AreEqual((float)((Rate / 2) - heard.Length + n) / (Rate * 10), heard[n]);
    }

    [TestMethod]
    public void TheEndStopsPlaybackWhileStretching()
    {
        using var output = new FakeOutput();
        using var player = Player(output, seconds: 0.1);
        player.PitchCorrection = true;
        player.Speed = 2;

        player.Play();
        WaitUntil(() =>
        {
            output.Drain(output.QueuedFrames);
            return player.IsEnded;
        });

        Assert.IsNull(player.Error);
        Assert.AreEqual(0.1, player.Position.TotalSeconds, Stretched);
    }

    [TestMethod]
    public void AnOutputAtAnotherRateIsRefused()
    {
        var mixer = new StemMixer([KeyValuePair.Create("song", (IPcmSource)new FakeSource(44100, 2, 100, (_, _) => 0))]);
        using var output = new FakeOutput();

        Assert.ThrowsExactly<ArgumentException>(() => new StemPlayer(mixer, output));
    }

    [TestMethod]
    [DataRow(0.5f, true)]
    [DataRow(1f, false)]
    [DataRow(1.5f, false)]
    [TestCategory("Slow")]
    public void MetronomeClicksStayOnTheBeatOverAWholeSong(float speed, bool pitchCorrection)
    {
        // AC C10.1. Five minutes of noise at 120 BPM, silent for 60 ms either side of each beat so a click can be found
        // in the mixed output, with the grains free to move around it while stretched.
        const int seconds = 300;
        const int beat = Rate / 2;
        const int quiet = Rate * 60 / 1000;
        long frames = (long)seconds * Rate;
        float Noise(long n, int channel)
        {
            long fromBeat = n % beat;
            if (fromBeat < quiet || fromBeat > beat - quiet)
                return 0f;
            uint hash = (uint)(n * 2654435761L) ^ (uint)(channel * 40503);
            hash ^= hash >> 15;
            hash *= 2246822519;
            hash ^= hash >> 13;
            return ((hash & 0xFFFF) / 65535f * 0.6f) - 0.3f;
        }

        var tempoMap = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        var metronome = new MetronomeSchedule(tempoMap, TimeSpan.Zero, TimeSpan.FromSeconds(seconds), Rate);
        Assert.AreEqual((2 * seconds) + 1, metronome.Count, "A beat every half second, the end's included.");

        // Each click is where a run of silence first breaks near its beat.
        var onsets = new List<long>();
        long silentFor = 0;
        using var output = new FakeOutput
        {
            KeepsWritten = false,
            Written = (first, stereo) =>
            {
                for (int i = 0; i < stereo.Length / 2; i++)
                {
                    if (stereo[2 * i] == 0f)
                    {
                        silentFor++;
                        continue;
                    }

                    if (silentFor >= Rate / 40)
                        onsets.Add(first + i);
                    silentFor = 0;
                }
            },
        };

        var source = new FakeSource(Rate, 2, frames, Noise);
        using var player = new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)source)]), output, null, queueMilliseconds: 3000)
        {
            PitchCorrection = pitchCorrection,
            Speed = speed,
            Sounds = metronome,
        };
        player.Play();
        PlayToTheEnd(output, player, TimeSpan.FromSeconds(120));

        // Stretched, the output stands for the grains it plays, which a second stretcher over the same audio gives
        // frame for frame. Played as mixed, each frame written is a frame of the mix.
        var stretcher = pitchCorrection
            ? new TimeStretcher(Rate, new StemMixer([KeyValuePair.Create("song", (IPcmSource)new FakeSource(Rate, 2, frames, Noise))]).Mix) { Ratio = speed }
            : null;
        stretcher?.Reset(0);
        var skip = new float[Rate * 2];
        long stretched = 0;
        double SourceOf(long outputFrame)
        {
            if (stretcher is null)
                return outputFrame;
            while (stretched < outputFrame)
                stretched += stretcher.Read(skip.AsSpan(0, (int)Math.Min(Rate, outputFrame - stretched) * 2));
            return stretcher.PlayedPosition;
        }

        double worst = 0;
        double outputPerSource = pitchCorrection ? 1 / speed : 1;
        for (int k = 1; k < metronome.Count - 1; k++)
        {
            long at = metronome.FrameAt(k);
            long expected = (long)(at * outputPerSource);
            long onset = onsets.Where(o => Math.Abs(o - expected) < Rate * 40 / 1000).DefaultIfEmpty(-1).First();
            Assert.AreNotEqual(-1, onset, $"No click was found near beat {k}.");

            // A click's first sample is 0, as it's a sine from its start: it starts the frame before.
            worst = Math.Max(worst, Math.Abs(SourceOf(onset - 1) - at));
        }

        Assert.IsLessThanOrEqualTo(2.0 * Rate / 1000, worst, $"A click was {worst * 1000 / Rate:F2} ms from its beat.");
    }

    [TestMethod]
    public void SoundsAreLaidOverTheMixButNotKeptAsHeard()
    {
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 1);
        player.Sounds = new Schedule(new ScheduledSound(Rate / 10, Constant(0.25f, Rate / 100)));
        player.Play();
        PlayFor(output, Rate / 5);
        WaitUntil(() => output.QueuedFrames > 0);

        float[] written = output.WrittenLeft();
        Assert.AreEqual(0f, written[(Rate / 10) - 1]);
        Assert.AreEqual(0.25f, written[Rate / 10], 1e-6f);
        Assert.AreEqual(0.25f, written[(Rate / 10) + (Rate / 100) - 1], 1e-6f);
        Assert.AreEqual(0f, written[(Rate / 10) + (Rate / 100)]);

        var heard = new float[Rate / 5];
        Assert.IsTrue(player.TryReadHeard(heard, out _));
        Assert.IsTrue(heard.All(sample => sample == 0f), "The sound was kept as heard.");
    }

    [TestMethod]
    public void SoundVolumeScalesTheSoundsAndTheMixIsClamped()
    {
        using var output = new FakeOutput();
        using var player = new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)new FakeSource(Rate, 2, Rate, (_, _) => 0.9f))]), output)
        {
            Sounds = new Schedule(new ScheduledSound(0, Constant(0.25f, Rate / 100)), new ScheduledSound(Rate / 2, Constant(0.25f, Rate / 100))),
            SoundVolume = 0.2f,
        };
        player.Play();
        PlayFor(output, Rate / 4);
        player.SoundVolume = 2;
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames > 0);

        float[] written = output.WrittenLeft();
        Assert.AreEqual(0.95f, written[0], 1e-6f);
        Assert.AreEqual(1f, written[Rate / 2], "Past full scale, it's clamped.");
    }

    [TestMethod]
    public void ASeekDropsTheSoundsPlaying()
    {
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10);
        player.Sounds = new Schedule(new ScheduledSound(0, Constant(0.25f, Rate)));
        player.Play();
        PlayFor(output, Rate / 10);
        Assert.AreEqual(0.25f, output.WrittenLeft()[Rate / 20], 1e-6f);

        int clears = output.Clears;
        player.Seek(TimeSpan.FromSeconds(5));
        WaitUntil(() => output.Clears > clears && output.QueuedFrames >= QueueTarget);

        float[] written = output.WrittenLeft();
        Assert.AreEqual(0f, output.FirstSampleAfterLastClear);
        Assert.IsTrue(written[^QueueTarget..].All(sample => sample == 0f), "The sound played on after the seek.");
    }

    [TestMethod]
    public void ANewScheduleLetsTheSoundsPlayingPlayOut()
    {
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10);
        player.Sounds = new Schedule(new ScheduledSound(0, Constant(0.25f, Rate)), new ScheduledSound(2 * Rate, Constant(0.5f, Rate / 10)));
        player.Play();
        PlayFor(output, Rate / 10);

        player.Sounds = new Schedule(new ScheduledSound(3 * Rate, Constant(0.125f, Rate / 10)));
        PlayFor(output, 4 * Rate);
        WaitUntil(() => output.QueuedFrames > 0);

        float[] written = output.WrittenLeft();
        Assert.AreEqual(0.25f, written[Rate - 1], 1e-6f, "The sound playing was cut short.");
        Assert.AreEqual(0f, written[Rate]);
        Assert.AreEqual(0f, written[2 * Rate], "The old schedule's sound played.");
        Assert.AreEqual(0.125f, written[3 * Rate], 1e-6f, "The new schedule's sound didn't play.");
    }

    [TestMethod]
    public void PlayedAsMixedASoundIsReadAtTheSpeedSoItKeepsItsLength()
    {
        // The device plays what's written twice as fast, so the sound is written over twice as many frames, to be heard
        // as long as it is and at its own pitch.
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10);
        player.Speed = 2;
        player.Sounds = new Schedule(new ScheduledSound(Rate, Constant(0.25f, Rate / 10)));
        player.Play();
        PlayFor(output, Rate);
        WaitUntil(() => output.QueuedFrames > 0);

        float[] written = output.WrittenLeft();
        int length = written.Count(sample => sample != 0f);
        Assert.AreEqual(Rate / 5, length, 1);
        Assert.AreEqual(0f, written[Rate - 1]);
        Assert.AreNotEqual(0f, written[Rate]);
    }

    [TestMethod]
    public void WhilePausedTheSmoothPositionIsThePosition()
    {
        var clock = new ManualClock();
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10, clock);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        PlayFor(output, Rate / 4);
        player.Pause();

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(player.Position, player.SmoothPosition);
    }

    [TestMethod]
    public void TheSmoothPositionMovesOnBetweenTheDevicesTakings()
    {
        var clock = new ManualClock();
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10, clock);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        Assert.AreEqual(TimeSpan.Zero, player.SmoothPosition);

        // The device takes 10 ms every 10 ms: between, it moves on with the clock, and lands on each taking.
        for (int step = 1; step <= 20; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(4));
            Assert.AreEqual((step - 1) * 10 + 4, player.SmoothPosition.TotalMilliseconds, 1e-3);
            clock.Advance(TimeSpan.FromMilliseconds(6));
            output.Drain(Rate / 100);
            WaitUntil(() => output.QueuedFrames >= QueueTarget);
            Assert.AreEqual(step * 10, player.SmoothPosition.TotalMilliseconds, 1e-3);
            Assert.AreEqual(player.Position.TotalMilliseconds, player.SmoothPosition.TotalMilliseconds, 1e-3);
        }
    }

    [TestMethod]
    public void TheSmoothPositionFollowsAPositionThatRunsSlowerThanTheClock()
    {
        var clock = new ManualClock();
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10, clock);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        _ = player.SmoothPosition;

        // The device takes 10 ms every 12 ms. Each taking pulls the smooth position halfway back to it, so it runs no
        // more than 4 ms ahead, and steps back no more than 2 ms at a time.
        double last = 0;
        for (int step = 1; step <= 100; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(12));
            output.Drain(Rate / 100);
            WaitUntil(() => output.QueuedFrames >= QueueTarget);

            double smooth = player.SmoothPosition.TotalMilliseconds;
            double position = player.Position.TotalMilliseconds;
            Assert.IsLessThanOrEqualTo(4.0 + 1e-3, smooth - position, $"Step {step}: {smooth} ms against {position} ms.");
            Assert.IsGreaterThanOrEqualTo(-1e-3, smooth - position, $"Step {step}: {smooth} ms against {position} ms.");
            Assert.IsGreaterThanOrEqualTo(last - 2.0 - 1e-3, smooth, $"Step {step} stepped back from {last} ms to {smooth} ms.");
            last = smooth;
        }
    }

    [TestMethod]
    public void ASeekPutsTheSmoothPositionStraightThere()
    {
        var clock = new ManualClock();
        using var output = new FakeOutput();
        using var player = SilentPlayer(output, seconds: 10, clock);
        player.Play();
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        _ = player.SmoothPosition;
        clock.Advance(TimeSpan.FromMilliseconds(30));
        PlayFor(output, Rate / 50);

        int clears = output.Clears;
        player.Seek(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TimeSpan.FromSeconds(5), player.SmoothPosition);
        WaitUntil(() => output.Clears > clears && output.QueuedFrames >= QueueTarget);
        clock.Advance(TimeSpan.FromMilliseconds(30));
        Assert.AreEqual(5000, player.SmoothPosition.TotalMilliseconds, 1e-3);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        Assert.AreEqual(5005, player.SmoothPosition.TotalMilliseconds, 1e-3);
    }

    /// <summary>A player of one stem whose value rises from 0 to 1 over <paramref name="seconds"/>.</summary>
    private static StemPlayer Player(FakeOutput output, double seconds)
    {
        long frames = (long)(seconds * Rate);
        var source = new FakeSource(Rate, 2, frames, (n, _) => (float)n / (Rate * 10));
        return new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)source)]), output);
    }

    /// <summary>A player of one silent stem <paramref name="seconds"/> long.</summary>
    private static StemPlayer SilentPlayer(FakeOutput output, double seconds, TimeProvider? clock = null)
    {
        var source = new FakeSource(Rate, 2, (long)(seconds * Rate), (_, _) => 0f);
        return new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)source)]), output, clock);
    }

    /// <summary>A sound of <paramref name="frames"/> frames at <paramref name="value"/>.</summary>
    private static SynthSound Constant(float value, int frames) => new([.. Enumerable.Repeat(value, frames)], Rate);

    /// <summary>Has the device play everything the player queues until it reaches the end.</summary>
    private static void PlayToTheEnd(FakeOutput output, StemPlayer player, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!player.IsEnded)
        {
            if (clock.Elapsed > timeout)
                Assert.Fail("Timed out playing to the end.");
            output.Drain(output.QueuedFrames);
            Thread.Sleep(1);
        }

        Assert.IsNull(player.Error);
    }

    /// <summary>Has the device play <paramref name="frames"/> frames, as the player keeps queuing them.</summary>
    private static void PlayFor(FakeOutput output, int frames)
    {
        while (frames > 0)
        {
            WaitUntil(() => output.QueuedFrames > 0);
            int count = Math.Min(frames, output.QueuedFrames);
            output.Drain(count);
            frames -= count;
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("Timed out waiting for the player.");
            Thread.Sleep(2);
        }
    }

    /// <summary>The sounds given, in order.</summary>
    private sealed class Schedule(params ScheduledSound[] sounds) : ISoundSchedule
    {
        public void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into) =>
            into.AddRange(sounds.Where(s => s.SourceFrame >= fromSourceFrame && s.SourceFrame < toSourceFrame));
    }

    /// <summary>A clock that only moves when told to.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));
    }

    /// <summary>Is given what's written, from the frame it starts at.</summary>
    private delegate void WriteHandler(long firstFrame, ReadOnlySpan<float> stereo);

    /// <summary>An output whose queue only drains when <see cref="Drain"/> says the device played it.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private readonly Lock _lock = new();
        private int _queued;
        private int _largestWrite;
        private float? _firstAfterClear;
        private readonly List<float> _writtenLeft = [];
        private long _writtenFrames;

        public int SampleRate => Rate;

        public bool FailWrites { get; init; }

        /// <summary>Whether what's written is kept for <see cref="WrittenLeft"/>; off for tests that play minutes of it.</summary>
        public bool KeepsWritten { get; init; } = true;

        /// <summary>Given everything written, as it's written.</summary>
        public WriteHandler? Written { get; init; }

        public bool IsPlaying { get; private set; }

        public bool IsDisposed { get; private set; }

        public int Clears { get; private set; }

        public float Volume { get; set; } = 1;

        public float Speed { get; set; } = 1;

        /// <summary>The most frames written at once.</summary>
        public int LargestWrite
        {
            get
            {
                lock (_lock)
                    return _largestWrite;
            }
        }

        /// <summary>The first left sample written since the last <see cref="Clear"/>, or since the start.</summary>
        public float? FirstSampleAfterLastClear
        {
            get
            {
                lock (_lock)
                    return _firstAfterClear;
            }
        }

        public int QueuedFrames
        {
            get
            {
                lock (_lock)
                    return _queued;
            }
        }

        /// <summary>How many frames have been written since the start.</summary>
        public long WrittenFrames
        {
            get
            {
                lock (_lock)
                    return _writtenFrames;
            }
        }

        /// <summary>The left channel of everything written since the start.</summary>
        public float[] WrittenLeft()
        {
            lock (_lock)
                return [.. _writtenLeft];
        }

        public void Write(ReadOnlySpan<float> stereo)
        {
            if (FailWrites)
                throw new AudioDeviceException("The device went away.");

            lock (_lock)
            {
                if (_queued == 0 && _firstAfterClear is null)
                    _firstAfterClear = stereo[0];
                _queued += stereo.Length / 2;
                _largestWrite = Math.Max(_largestWrite, stereo.Length / 2);
                Written?.Invoke(_writtenFrames, stereo);
                _writtenFrames += stereo.Length / 2;
                if (KeepsWritten)
                {
                    for (int i = 0; i < stereo.Length; i += 2)
                        _writtenLeft.Add(stereo[i]);
                }
            }
        }

        public void Drain(int frames)
        {
            lock (_lock)
                _queued = Math.Max(0, _queued - frames);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _queued = 0;
                _firstAfterClear = null;
                Clears++;
            }
        }

        public void Pause() => IsPlaying = false;

        public void Resume() => IsPlaying = true;

        public void Dispose() => IsDisposed = true;
    }
}
