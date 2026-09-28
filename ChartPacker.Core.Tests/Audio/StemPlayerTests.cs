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
        Assert.IsTrue(output.QueuedFrames < QueueTarget + (Rate / 20), $"{output.QueuedFrames} frames were queued.");
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
        private float? _firstAfterClear;

        public int SampleRate => Rate;

        public bool FailWrites { get; init; }

        public bool IsPlaying { get; private set; }

        public bool IsDisposed { get; private set; }

        public int Clears { get; private set; }

        public float Volume { get; set; } = 1;

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

        public void Write(ReadOnlySpan<float> stereo)
        {
            if (FailWrites)
                throw new AudioDeviceException("The device went away.");

            lock (_lock)
            {
                if (_queued == 0 && _firstAfterClear is null)
                    _firstAfterClear = stereo[0];
                _queued += stereo.Length / 2;
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
