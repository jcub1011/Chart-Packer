using ChartPacker.Audio.Dsp;
using ChartPacker.Audio.Mixing;
using ChartPacker.Audio.Spectrum;

namespace ChartPacker.Audio.Playback;

/// <summary>
/// Plays a <see cref="StemMixer"/> through an <see cref="IAudioOutput"/>. A background thread
/// keeps about <see cref="QueueMilliseconds"/> of mixed audio queued; everything else is
/// requested from the caller's thread and picked up by that thread.
/// </summary>
/// <remarks>
/// A change to a stem's switches is heard once the audio already queued has played, so after at
/// most <see cref="QueueMilliseconds"/>: the queue holds that long of playback at any
/// <see cref="Speed"/>. Seeking drops the queue, so it's heard at once. With
/// <see cref="PitchCorrection"/> on, the mix goes through a <see cref="TimeStretcher"/> on its
/// way to the output, which mixes what it holds again when a switch changes, so the switch is heard
/// less than 40 ms later than that, as the grain it was putting together fades out. The player owns
/// the output but not the mixer's sources; dispose it before them.
/// </remarks>
public sealed class StemPlayer : IDisposable
{
    /// <summary>How much mixed audio is kept queued ahead of the device.</summary>
    public const int QueueMilliseconds = 150;

    private const int ChunkMilliseconds = 20;

    private readonly StemMixer _mixer;
    private readonly IAudioOutput _output;
    private readonly TimeStretcher _stretcher;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly float[] _chunk;
    private readonly int _queueTarget;

    // The fewest frames mixed at a time, so a very slow speed doesn't mix a few frames at a time.
    private readonly int _minimumChunk;

    // All guarded by _lock.
    private bool _playing;
    private bool _ended;
    private bool _disposed;
    private long? _pendingSeek;
    private string? _error;
    private float _speed;
    private bool _pitchCorrection;

    // A new Speed or PitchCorrection for the playback thread to take up without dropping the queue.
    private bool _retune;

    // Frames written to the output since it was opened: what's been played is this less what's still queued.
    private long _writtenFrames;

    // Whether the mix goes through the stretcher, which plays it at the device's own speed. Set on the playback thread,
    // under _lock.
    private bool _stretching;

    // The stems' switches as the stretcher's input was mixed with. Playback thread only.
    private int _mixedSwitches;

    // Which frame of the mix each frame written stands for. Under _lock, with _writtenFrames.
    private readonly PositionMap _map = new();

    // What was written lately, by output frame, for TryReadHeard. Written and read under _lock, with _writtenFrames.
    private readonly OutputTap _tap = new();
    private bool _capturesHeard = true;

    /// <param name="mixer">What to play.</param>
    /// <param name="output">Where to play it, at <paramref name="mixer"/>'s rate. The player owns it.</param>
    public StemPlayer(StemMixer mixer, IAudioOutput output)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(output);
        if (output.SampleRate != mixer.OutputRate)
            throw new ArgumentException("The output's rate must be the mixer's.", nameof(output));

        _mixer = mixer;
        _output = output;
        _speed = float.IsFinite(output.Speed) && output.Speed > 0 ? output.Speed : 1;
        _stretcher = new TimeStretcher(mixer.OutputRate, mixer.Mix);
        _chunk = new float[mixer.OutputRate * ChunkMilliseconds / 1000 * 2];
        _queueTarget = mixer.OutputRate * QueueMilliseconds / 1000;
        _minimumChunk = Math.Max(1, mixer.OutputRate / 1000);
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio playback" };
        _thread.Start();
    }

    /// <summary>
    /// Raised on the playback thread when playback stops by itself: at the end of the song, or
    /// because of <see cref="Error"/>.
    /// </summary>
    public event EventHandler? Stopped;

    public StemMixer Mixer => _mixer;

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _playing;
        }
    }

    /// <summary>Playback reached the end of the song; <see cref="Play"/> starts it again.</summary>
    public bool IsEnded
    {
        get
        {
            lock (_lock)
                return _ended;
        }
    }

    /// <summary>Why playback stopped, if the audio or the device failed.</summary>
    public string? Error
    {
        get
        {
            lock (_lock)
                return _error;
        }
    }

    /// <summary>The audio's own length, or as much of it as is known; <see cref="MinimumLength"/> left out.</summary>
    public TimeSpan Length => FramesToTime(_mixer.LengthFrames >= 0 ? _mixer.LengthFrames : _mixer.KnownLengthFrames);

    /// <summary>
    /// How long playback runs for at least: past the end of the audio it plays on in silence until here, so what
    /// follows the clock (a chart, a video) can run on past the audio. Shorter than the audio, it changes nothing.
    /// </summary>
    public TimeSpan MinimumLength
    {
        get => FramesToTime(_mixer.MinimumLengthFrames);
        set => _mixer.MinimumLengthFrames = Math.Max(0, (long)(value.TotalSeconds * _mixer.OutputRate));
    }

    /// <summary>What's being heard now.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_pendingSeek is { } target)
                    return FramesToTime(target);
                return FramesToTime(HeardSourceFrame());
            }
        }
    }

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = value;
    }

    /// <summary>
    /// How fast playback runs, from 0.01 to 100; 1 plays at the audio's own speed. The pitch moves with it unless
    /// <see cref="PitchCorrection"/> is on. Without it, a change is heard at once, queued audio included; with it, what
    /// follows the queue is stretched to the new speed, so it's heard once the queue has played, without a break.
    /// <see cref="Position"/> stays in the audio's own time.
    /// </summary>
    public float Speed
    {
        get
        {
            lock (_lock)
                return _speed;
        }
        set
        {
            float speed = float.IsFinite(value) ? Math.Clamp(value, (float)TimeStretcher.MinRatio, (float)TimeStretcher.MaxRatio) : 1;
            lock (_lock)
            {
                if (speed == _speed)
                    return;
                _speed = speed;
                ApplySpeed();
            }
        }
    }

    /// <summary>
    /// Whether a <see cref="Speed"/> other than 1 keeps the audio's pitch, by stretching it in time, instead of playing it
    /// faster and higher or slower and lower. Off by default. Switching it is heard at once.
    /// </summary>
    public bool PitchCorrection
    {
        get
        {
            lock (_lock)
                return _pitchCorrection;
        }
        set
        {
            lock (_lock)
            {
                if (value == _pitchCorrection)
                    return;
                _pitchCorrection = value;
                ApplySpeed();
            }
        }
    }

    /// <summary>
    /// Whether what's played is kept for <see cref="TryReadHeard"/>. Off, the playback thread doesn't keep it,
    /// and once it's on again nothing is heard until enough has been played since.
    /// </summary>
    public bool CapturesHeard
    {
        get
        {
            lock (_lock)
                return _capturesHeard;
        }
        set
        {
            lock (_lock)
                _capturesHeard = value;
        }
    }

    /// <summary>
    /// Copies the <paramref name="mono"/>.Length frames heard last, channels averaged: what goes to the output,
    /// before <see cref="Volume"/>.
    /// </summary>
    /// <param name="sampleRate">
    /// The rate they're heard at: the mixer's, times the speed while the device plays the mix faster or slower rather
    /// than it being stretched in time.
    /// </param>
    /// <returns>
    /// <see langword="false"/>, leaving <paramref name="mono"/> as it was, while nothing is playing, during a seek,
    /// while <see cref="CapturesHeard"/> is off, or until that much has been heard since the start, the last seek
    /// or it was turned on.
    /// </returns>
    public bool TryReadHeard(Span<float> mono, out double sampleRate)
    {
        lock (_lock)
        {
            // While the mix is stretched, the device plays at its own speed.
            sampleRate = _mixer.OutputRate * (double)_output.Speed;
            if (_disposed || !_playing || !_capturesHeard || _pendingSeek is not null)
                return false;
            return _tap.TryRead(Math.Max(0, _writtenFrames - _output.QueuedFrames), mono);
        }
    }

    /// <summary>Starts or resumes playing; after the end, from the start.</summary>
    public void Play()
    {
        lock (_lock)
        {
            if (_disposed || _error is not null)
                return;

            if (_ended)
            {
                _pendingSeek = 0;
                _ended = false;
            }

            _playing = true;
            _output.Resume();
            Monitor.PulseAll(_lock);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _playing = false;
            _output.Pause();
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Moves playback to <paramref name="position"/>, playing or not.</summary>
    public void Seek(TimeSpan position)
    {
        long frame = Math.Max(0, (long)(position.TotalSeconds * _mixer.OutputRate));
        lock (_lock)
        {
            if (_disposed || _error is not null)
                return;

            _pendingSeek = frame;
            _ended = false;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Stops the playback thread and closes the output.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _playing = false;
            Monitor.PulseAll(_lock);
        }

        _thread.Join();
        _output.Dispose();
    }

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / _mixer.OutputRate);

    /// <summary>The frame of the mix being heard now. Under <see cref="_lock"/>.</summary>
    private long HeardSourceFrame() => (long)Math.Round(_map.SourceAt(Math.Max(0, _writtenFrames - _output.QueuedFrames)));

    /// <summary>
    /// Whether the queue can be kept through a new <see cref="Speed"/> or <see cref="PitchCorrection"/>, which it can
    /// while the device's speed stays as it is: stretched, the device plays at its own speed, so the mix can go on being
    /// stretched at the new speed, 1 included, as long as it's to be stretched or is to play at its own speed; played as
    /// mixed at the device's own speed, it can start being stretched. Under <see cref="_lock"/>.
    /// </summary>
    private bool KeepsQueue() => _stretching ? _pitchCorrection || _speed == 1 : _output.Speed == 1;

    /// <summary>
    /// Takes up a new <see cref="Speed"/> or <see cref="PitchCorrection"/>. Played as mixed before and after, the device
    /// just plays the queue at the new speed. Otherwise, while the queue can be kept, the playback thread stretches what
    /// follows it at the new speed; when it can't, as the device's speed has to change, playback starts again from what's
    /// heard now. Under <see cref="_lock"/>.
    /// </summary>
    private void ApplySpeed()
    {
        bool stretches = _pitchCorrection && _speed != 1;
        if (!stretches && !_stretching)
        {
            _output.Speed = _speed;
            return;
        }

        if (_disposed || _error is not null)
            return;

        if (KeepsQueue())
            _retune = true;
        else
            _pendingSeek ??= HeardSourceFrame();
        Monitor.PulseAll(_lock);
    }

    /// <summary>Takes up what <see cref="ApplySpeed"/> asked for without dropping the queue. On the playback thread, under <see cref="_lock"/>.</summary>
    private void Retune()
    {
        if (!_stretching && !(_pitchCorrection && _speed != 1))
            return;

        if (!KeepsQueue())
        {
            _pendingSeek ??= HeardSourceFrame();
            return;
        }

        _stretcher.Ratio = _speed;
        if (!_stretching)
        {
            // What's queued plays as mixed at the device's own speed, and is carried on from, stretched.
            _stretcher.Reset(_mixer.PositionFrames);
            _mixedSwitches = _mixer.SwitchVersion;
            _stretching = true;
        }
    }

    private void Run()
    {
        while (true)
        {
            long? seek;
            lock (_lock)
            {
                while (!_disposed && !_playing && _pendingSeek is null && !_retune)
                    Monitor.Wait(_lock);
                if (_disposed)
                    return;

                if (_pendingSeek is null && _retune)
                {
                    _retune = false;
                    Retune();
                    if (!_playing && _pendingSeek is null)
                        continue;
                }

                seek = _pendingSeek;
            }

            try
            {
                if (seek is { } frame)
                {
                    _output.Clear();
                    _mixer.Seek(frame);
                    lock (_lock)
                    {
                        // The speed is taken up here, with the queue empty, so everything queued from now on is at it.
                        _stretching = _pitchCorrection && _speed != 1;
                        _output.Speed = _stretching ? 1 : _speed;
                        _map.Reset(_writtenFrames, _mixer.PositionFrames);
                        _tap.Clear();
                        _retune = false;
                        if (_pendingSeek == seek)
                            _pendingSeek = null;

                        if (_stretching)
                        {
                            _stretcher.Ratio = _speed;
                            _stretcher.Reset(_mixer.PositionFrames);
                        }
                    }

                    _mixedSwitches = _mixer.SwitchVersion;
                    continue;
                }

                // The queue is counted in frames as written, which the device plays faster or slower with its speed (1 while
                // the mix is stretched), so it's kept to as many as play in QueueMilliseconds; and no chunk is longer, so a
                // slow speed doesn't queue seconds.
                int target = QueueTarget();
                if (_output.QueuedFrames >= target)
                {
                    Wait();
                    continue;
                }

                // The stretcher holds mix it hasn't played yet, as long as a second at a slow speed: a switched stem has it
                // mixed again, so the switch is heard once the queue has played rather than once that has too.
                int switches = _mixer.SwitchVersion;
                if (_stretching && switches != _mixedSwitches)
                {
                    _mixedSwitches = switches;
                    _mixer.Seek(_stretcher.ForgetInput());
                }

                var chunk = _chunk.AsSpan(0, Math.Min(_chunk.Length, target * 2));
                double from = _stretching ? _stretcher.SourcePosition : _mixer.PositionFrames;
                int frames = _stretching ? _stretcher.Read(chunk) : _mixer.Mix(chunk);
                if (frames > 0)
                {
                    double to = _stretching ? _stretcher.SourcePosition : _mixer.PositionFrames;

                    // Together, so Position never sees one without the other. Queuing only copies.
                    lock (_lock)
                    {
                        _output.Write(_chunk.AsSpan(0, frames * 2));
                        if (_capturesHeard)
                            _tap.Write(_chunk.AsSpan(0, frames * 2), _writtenFrames);
                        _map.Add(_writtenFrames, frames, from, to);
                        _writtenFrames += frames;
                    }
                }
                else if (_output.QueuedFrames == 0)
                {
                    Stop(ended: true, error: null);
                }
                else
                {
                    Wait();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AudioDeviceException)
            {
                Stop(ended: false, error: ex.Message);
            }
        }
    }

    /// <summary>How many frames, as mixed, play in <see cref="QueueMilliseconds"/> at the current <see cref="Speed"/>; at least a millisecond's.</summary>
    private int QueueTarget()
    {
        float speed = _output.Speed;
        if (!float.IsFinite(speed) || speed <= 0)
            speed = 1;
        return Math.Max(_minimumChunk, (int)(_queueTarget * (double)speed));
    }

    /// <summary>Waits a little for the device to drain the queue, or for a request.</summary>
    private void Wait()
    {
        lock (_lock)
        {
            if (!_disposed && _pendingSeek is null && !_retune && _playing)
                Monitor.Wait(_lock, 5);
        }
    }

    private void Stop(bool ended, string? error)
    {
        lock (_lock)
        {
            // A request that came in meanwhile wins: a seek back from the end carries on playing.
            if (_disposed || _pendingSeek is not null)
                return;

            _playing = false;
            _ended = ended;
            _error = error;
            _output.Pause();
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }
}
