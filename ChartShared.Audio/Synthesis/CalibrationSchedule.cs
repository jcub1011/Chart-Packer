using ChartShared.Audio.Playback;

namespace ChartShared.Audio.Synthesis;

/// <summary>
/// Which beats' beeps have been handed to the player, shared by the <see cref="CalibrationSchedule"/>s that follow one
/// another, so a beep already given isn't given again when a new schedule moves it a little.
/// </summary>
public sealed class CalibrationBeepsGiven
{
    private long _last = -1;

    /// <summary>The last beat whose beep was given, or -1 for none.</summary>
    public long Last => Interlocked.Read(ref _last);

    internal void Give(long beat) => Interlocked.Exchange(ref _last, Math.Max(beat, Last));
}

/// <summary>
/// The calibration tool's beeps (charter-spec C11 item 1), laid over a silent mix through
/// <see cref="StemPlayer.Sounds"/>, so they have exactly a chart's queueing and device latency.
/// </summary>
/// <remarks>
/// <para>
/// The beats are on the app's monotonic clock (<see cref="CalibrationBeats"/>). The anchor ties that clock to the
/// mix: at <c>anchorClock</c>, the player's <see cref="StemPlayer.SmoothPosition"/> was <c>anchorSong</c>, and at
/// 100% it moves one second a second. Each beep is therefore placed at the frame that will leave SDL's queue at its
/// beat's time less the latency: changing the latency moves the beep, never the flash.
/// </para>
/// <para>
/// A schedule never changes once a player has it. For a new latency, or to re-anchor against the device's clock
/// drifting from the system's, a new one is handed over, sharing the old one's <see cref="CalibrationBeepsGiven"/>:
/// the beeps already queued play as they were, and the change is heard from the next beat not yet queued. A beep
/// that a larger latency moves to before what's already queued is skipped, once.
/// </para>
/// </remarks>
public sealed class CalibrationSchedule : ISoundSchedule
{
    private readonly double _anchorClock;
    private readonly double _anchorSong;
    private readonly double _latency;
    private readonly int _sampleRate;
    private readonly SynthSound _beep;
    private readonly SynthSound _markedBeep;
    private readonly CalibrationBeepsGiven _given;

    /// <param name="beats">The beats to beep on.</param>
    /// <param name="anchorClock">A time on the monotonic clock, in seconds…</param>
    /// <param name="anchorSong">…and where the player's smooth position was then, in seconds of the mix.</param>
    /// <param name="latency">How much earlier than its beat each beep is written, in seconds.</param>
    /// <param name="beep">The beep on a beat (<see cref="Synth.CalibrationBeep"/>).</param>
    /// <param name="markedBeep">The beep on a bar's first beat.</param>
    /// <param name="given">The beeps given by the schedules before this one, for the same beats; none if not given.</param>
    public CalibrationSchedule(
        CalibrationBeats beats,
        double anchorClock,
        double anchorSong,
        double latency,
        SynthSound beep,
        SynthSound markedBeep,
        CalibrationBeepsGiven? given = null)
    {
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(beep);
        ArgumentNullException.ThrowIfNull(markedBeep);
        if (beep.SampleRate != markedBeep.SampleRate)
            throw new ArgumentException("Both beeps must be at one rate.", nameof(markedBeep));

        Beats = beats;
        _anchorClock = anchorClock;
        _anchorSong = anchorSong;
        _latency = latency;
        _sampleRate = beep.SampleRate;
        _beep = beep;
        _markedBeep = markedBeep;
        _given = given ?? new CalibrationBeepsGiven();
    }

    public CalibrationBeats Beats { get; }

    /// <summary>The frame of the mix the beep for <paramref name="beat"/> starts at.</summary>
    public long FrameOf(long beat) =>
        (long)Math.Round((_anchorSong + (Beats.TimeOf(beat) - _latency - _anchorClock)) * _sampleRate);

    public void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        // The beat whose beep is at fromSourceFrame, a beat early against rounding, and after those already given.
        double fromClock = ((double)fromSourceFrame / _sampleRate) - _anchorSong + _latency + _anchorClock;
        long beat = Math.Max(_given.Last + 1, Math.Max(0, Beats.BeatAtOrBefore(fromClock)));
        for (; Beats.Contains(beat); beat++)
        {
            long frame = FrameOf(beat);
            if (frame >= toSourceFrame)
                break;
            if (frame < fromSourceFrame)
                continue;

            into.Add(new ScheduledSound(frame, Beats.IsMarked(beat) ? _markedBeep : _beep));
            _given.Give(beat);
        }
    }
}
