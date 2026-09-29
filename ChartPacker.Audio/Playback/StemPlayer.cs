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
/// most <see cref="QueueMilliseconds"/>. Seeking drops the queue, so it's heard at once. The
/// player owns the output but not the mixer's sources; dispose it before them.
/// </remarks>
public sealed class StemPlayer : IDisposable
{
    /// <summary>How much mixed audio is kept queued ahead of the device.</summary>
    public const int QueueMilliseconds = 150;

    private const int ChunkMilliseconds = 20;

    private readonly StemMixer _mixer;
    private readonly IAudioOutput _output;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly float[] _chunk;
    private readonly int _queueTarget;

    // All guarded by _lock.
    private bool _playing;
    private bool _ended;
    private bool _disposed;
    private long? _pendingSeek;
    private string? _error;

    // Frames written to the output: what's been played is this less what's still queued.
    private long _mixedFrames;

    // What was written lately, for TryReadHeard. Written and read under _lock, with _mixedFrames.
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
        _chunk = new float[mixer.OutputRate * ChunkMilliseconds / 1000 * 2];
        _queueTarget = mixer.OutputRate * QueueMilliseconds / 1000;
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

    /// <summary>The song's length, or as much of it as is known.</summary>
    public TimeSpan Length => FramesToTime(_mixer.LengthFrames >= 0 ? _mixer.LengthFrames : _mixer.KnownLengthFrames);

    /// <summary>What's being heard now.</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_pendingSeek is { } target)
                    return FramesToTime(target);
                return FramesToTime(Math.Max(0, _mixedFrames - _output.QueuedFrames));
            }
        }
    }

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = value;
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
    /// Copies the <paramref name="mono"/>.Length frames heard last, channels averaged: the mix as it leaves
    /// the mixer, before <see cref="Volume"/>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/>, leaving <paramref name="mono"/> as it was, while nothing is playing, during a seek,
    /// while <see cref="CapturesHeard"/> is off, or until that much has been heard since the start, the last seek
    /// or it was turned on.
    /// </returns>
    public bool TryReadHeard(Span<float> mono)
    {
        lock (_lock)
        {
            if (_disposed || !_playing || !_capturesHeard || _pendingSeek is not null)
                return false;
            return _tap.TryRead(Math.Max(0, _mixedFrames - _output.QueuedFrames), mono);
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

    private void Run()
    {
        while (true)
        {
            long? seek;
            lock (_lock)
            {
                while (!_disposed && !_playing && _pendingSeek is null)
                    Monitor.Wait(_lock);
                if (_disposed)
                    return;

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
                        _mixedFrames = _mixer.PositionFrames;
                        _tap.Clear();
                        if (_pendingSeek == seek)
                            _pendingSeek = null;
                    }

                    continue;
                }

                if (_output.QueuedFrames >= _queueTarget)
                {
                    Wait();
                    continue;
                }

                int frames = _mixer.Mix(_chunk);
                if (frames > 0)
                {
                    // Together, so Position never sees one without the other. Queuing only copies.
                    lock (_lock)
                    {
                        _output.Write(_chunk.AsSpan(0, frames * 2));
                        if (_capturesHeard)
                            _tap.Write(_chunk.AsSpan(0, frames * 2), _mixedFrames);
                        _mixedFrames += frames;
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

    /// <summary>Waits a little for the device to drain the queue, or for a request.</summary>
    private void Wait()
    {
        lock (_lock)
        {
            if (!_disposed && _pendingSeek is null && _playing)
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
