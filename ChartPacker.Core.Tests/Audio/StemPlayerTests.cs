using System.Diagnostics;
using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Playback;
using static ChartPacker.Core.Tests.Audio.TestAudio;

namespace ChartPacker.Core.Tests.Audio;

/// <summary><see cref="StemPlayer"/> against an output that only drains when a test says it has played.</summary>
[TestClass]
public sealed class StemPlayerTests
{
    private const int Rate = 48000;
    private const int QueueTarget = Rate * StemPlayer.QueueMilliseconds / 1000;

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
        Assert.AreEqual(1, player.Position.TotalSeconds, 0.001);
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
        Assert.AreEqual(0.25 * from, player.Position.TotalSeconds, 0.001);

        // What was queued, and stretched at the old speed meanwhile, plays out; what follows is at the new speed.
        PlayFor(output, QueueTarget * 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        double before = player.Position.TotalSeconds;
        PlayFor(output, Rate / 2);
        WaitUntil(() => output.QueuedFrames >= QueueTarget);
        player.Pause();

        Assert.AreEqual(before + 0.75, player.Position.TotalSeconds, 0.001);
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
        Assert.AreEqual(0.1, player.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void AnOutputAtAnotherRateIsRefused()
    {
        var mixer = new StemMixer([KeyValuePair.Create("song", (IPcmSource)new FakeSource(44100, 2, 100, (_, _) => 0))]);
        using var output = new FakeOutput();

        Assert.ThrowsExactly<ArgumentException>(() => new StemPlayer(mixer, output));
    }

    /// <summary>A player of one stem whose value rises from 0 to 1 over <paramref name="seconds"/>.</summary>
    private static StemPlayer Player(FakeOutput output, double seconds)
    {
        long frames = (long)(seconds * Rate);
        var source = new FakeSource(Rate, 2, frames, (n, _) => (float)n / (Rate * 10));
        return new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)source)]), output);
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

    /// <summary>An output whose queue only drains when <see cref="Drain"/> says the device played it.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private readonly Lock _lock = new();
        private int _queued;
        private int _largestWrite;
        private float? _firstAfterClear;
        private readonly List<float> _writtenLeft = [];

        public int SampleRate => Rate;

        public bool FailWrites { get; init; }

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
                    return _writtenLeft.Count;
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
                for (int i = 0; i < stereo.Length; i += 2)
                    _writtenLeft.Add(stereo[i]);
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
