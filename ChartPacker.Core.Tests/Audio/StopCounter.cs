using System.Diagnostics;
using ChartPacker.Audio.Playback;

namespace ChartPacker.Core.Tests.Audio;

/// <summary>
/// Counts a player's <see cref="StemPlayer.Stopped"/>, so a test waits for the event itself rather than for the state
/// it follows: the playback thread raises it after letting go of the lock it sets that state under, so a test that sees
/// <see cref="StemPlayer.IsPlaying"/> go false may not have been told yet.
/// </summary>
/// <remarks>
/// Once the event has been raised, the state it reports stays as it is until the test plays again. For how many times
/// it was raised in all, read <see cref="Count"/> after disposing the player, which joins the playback thread, so
/// nothing more can be raised.
/// </remarks>
internal sealed class StopCounter
{
    private readonly SemaphoreSlim _stopped = new(0);
    private int _count;

    public StopCounter(StemPlayer player)
    {
        player.Stopped += (_, _) =>
        {
            Interlocked.Increment(ref _count);
            _stopped.Release();
        };
    }

    /// <summary>How many times the player has stopped by itself.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Waits for the player's next stop, with <paramref name="device"/>, if given, playing what it's queued meanwhile.</summary>
    public void WaitForStop(FakeOutput? device = null)
    {
        var clock = Stopwatch.StartNew();
        while (!_stopped.Wait(TimeSpan.FromMilliseconds(2)))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("Timed out waiting for the player to stop.");
            device?.Drain(device.QueuedFrames);
        }
    }
}
