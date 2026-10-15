namespace ChartShared.Core.Tests.Audio;

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => 1_000_000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));
}
