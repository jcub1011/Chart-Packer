namespace ChartShared.Core.Input;

/// <summary>An action at its time in the song, as drawn (charter-spec C19 item 3).</summary>
/// <param name="Song">Its time in audio seconds, as the editor draws the song: <c>SmoothPosition − latency × speed</c> when it happened.</param>
/// <param name="Speed">The playback speed then, which turns real time into song time.</param>
public readonly record struct TimedAction(ActionEvent Input, double Song, double Speed)
{
    public InstrumentAction Action => Input.Action;

    public bool IsDown => Input.IsDown;
}

/// <summary>What takes timed actions: the playtest judge, record mode, and later a performance recorder (charter-spec C19 item 8).</summary>
public interface IActionSink
{
    void Handle(in TimedAction action);
}

/// <summary>
/// Maps a time on <see cref="InputClock"/> to the song as it was drawn then (charter-spec C19 item 3), from samples of
/// the playback clock taken each frame: the newest sample at or before the time, run on at the speed then. So an input is
/// placed by when it happened, not by when it was handled, and one from before a seek or the loop's jump back is placed
/// from before it.
/// </summary>
/// <remarks>Belongs to one thread, the one that samples and maps: the UI thread.</remarks>
public sealed class SongClockMap
{
    /// <summary>How far the song can be from where the last sample said it'd be before it's taken as a jump: a seek or the loop.</summary>
    public const double JumpTolerance = 0.1;

    private const int Capacity = 256;

    private readonly ClockSample[] _samples = new ClockSample[Capacity];
    private int _count;
    private int _newest = -1;

    /// <summary>Whether the last sample jumped from where the one before said the song would be.</summary>
    public bool Jumped { get; private set; }

    /// <summary>The newest sample's speed; 1 before any.</summary>
    public double Speed => _count == 0 ? 1 : _samples[_newest].Speed;

    /// <summary>Whether the song was playing at the newest sample.</summary>
    public bool IsPlaying => _count > 0 && _samples[_newest].Playing;

    /// <summary>The song's time at the newest sample.</summary>
    public double? Song => _count == 0 ? null : _samples[_newest].Song;

    /// <summary>Takes a sample: at <paramref name="time"/> on the input clock the song was drawn at <paramref name="song"/>.</summary>
    /// <param name="speed">The playback speed.</param>
    /// <param name="playing">Whether the song was playing; times while it's paused map to nothing.</param>
    public void Sample(double time, double song, double speed, bool playing)
    {
        Jumped = false;
        if (_count > 0)
        {
            var last = _samples[_newest];
            if (time < last.Time)
                return;
            if (playing && last.Playing)
            {
                double expected = last.Song + ((time - last.Time) * last.Speed);
                Jumped = Math.Abs(song - expected) > JumpTolerance;
            }
            else
            {
                Jumped = playing != last.Playing || Math.Abs(song - last.Song) > JumpTolerance;
            }
        }

        _newest = (_newest + 1) % Capacity;
        _samples[_newest] = new ClockSample(time, song, speed > 0 ? speed : 1, playing);
        _count = Math.Min(_count + 1, Capacity);
    }

    /// <summary>
    /// The song's time at <paramref name="time"/>; null when the song was paused then, or it's from before the oldest
    /// sample kept.
    /// </summary>
    public double? SongAt(double time)
    {
        for (int i = 0; i < _count; i++)
        {
            var sample = _samples[(_newest - i + Capacity) % Capacity];
            if (sample.Time <= time)
                return sample.Playing ? sample.Song + ((time - sample.Time) * sample.Speed) : null;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="input"/> at its time in the song, with the speed then; null when the song wasn't playing.
    /// </summary>
    public TimedAction? Map(in ActionEvent input)
    {
        for (int i = 0; i < _count; i++)
        {
            var sample = _samples[(_newest - i + Capacity) % Capacity];
            if (sample.Time <= input.Time)
            {
                return sample.Playing
                    ? new TimedAction(input, sample.Song + ((input.Time - sample.Time) * sample.Speed), sample.Speed)
                    : null;
            }
        }

        return null;
    }

    /// <summary>Forgets every sample.</summary>
    public void Clear()
    {
        _count = 0;
        _newest = -1;
        Jumped = false;
    }

    private readonly record struct ClockSample(double Time, double Song, double Speed, bool Playing);
}
