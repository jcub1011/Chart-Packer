using SDL;
using static SDL.SDL3;

namespace ChartShared.Audio.Playback;

/// <summary>
/// Plays audio on an output device through <see href="https://libsdl.org">SDL3</see>'s
/// audio subsystem (via <see href="https://github.com/ppy/SDL3-CS">ppy/SDL3-CS</see>; see
/// <c>THIRD-PARTY-NOTICES.md</c>). Nothing else of SDL is used.
/// </summary>
/// <remarks>
/// SDL converts the stream to whatever the device wants, and moves it to the new default device
/// when that changes, e.g. when headphones are unplugged. Opened by <see cref="SdlAudio.Open"/>,
/// which keeps SDL's audio subsystem running; opening and closing a device stream is safe from
/// any thread.
/// <para>
/// When an output device is chosen, the stream opens on that one and SDL leaves it there: a
/// device opened by name or ID is left alone when it disconnects, rather than moved to the new
/// default. So the output is checked about once a second, and when the chosen device is found to
/// be gone the stream is opened again on the default device (dropping what was queued), which plays
/// until the chosen device is connected again; the stream then moves back to it (dropping what was
/// queued, again). SDL moves a stream opened on the default device by itself.
/// </para>
/// <para>
/// SDL moves the stream without saying so, and only reports device events to an event loop this
/// app doesn't run, so <see cref="Device"/> is checked when playback starts and once a second.
/// </para>
/// <para>
/// The device carries the latency the app can read for it (<see cref="DeviceLatency"/>): SDL's device
/// buffer, and on Windows what WASAPI reports for the endpoint (issue #140). When the stream is on a
/// chosen device, that device's endpoint is read, not the default endpoint's.
/// </para>
/// </remarks>
public sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly Lock _deviceLock = new();
    private readonly Lock _checkGate = new();
    private readonly Timer _deviceCheck;
    private SDL_AudioStream* _stream;
    private AudioDevice _device;
    private (string Name, string? Id, TimeSpan? Latency)? _osLatency;
    // The device that was asked for, as AudioDevices identifies it; null or empty when the default device was.
    private readonly string? _preferredDevice;
    // The physical SDL device the stream is on, when it's the chosen one; null when it's on the default device.
    private uint? _openedDeviceId;
    // The chosen device's Windows endpoint ID, when the stream is on it; null when it's on the default device.
    private string? _endpointId;
    private readonly bool _lowLatencyAudio;
    private float _volume = 1;
    private float _speed = 1;
    // A device stream starts paused, until Resume.
    private bool _paused = true;
    private bool _disposed;

    /// <exception cref="AudioDeviceException">No output device could be opened.</exception>
    internal SdlAudioOutput(int sampleRate, string? outputDevice, bool lowLatencyAudio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _lowLatencyAudio = lowLatencyAudio;
        _preferredDevice = outputDevice;

        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = sampleRate };
        _stream = Open(&spec, outputDevice);
        if (_stream is null)
            throw new AudioDeviceException(SDL_GetError() ?? "No audio output device could be opened.");

        _device = ReadDevice() ?? AudioDevice.Unknown;
        _deviceCheck = new Timer(_ => CheckDevice(), null, DeviceCheckInterval, DeviceCheckInterval);
    }

    public int SampleRate { get; }

    public AudioDevice Device
    {
        get
        {
            lock (_deviceLock)
                return _device;
        }
    }

    public event Action<AudioDevice>? DeviceChanged;

    public int QueuedFrames
    {
        get
        {
            lock (_lock)
                return _stream is null ? 0 : Math.Max(0, SDL_GetAudioStreamQueued(_stream)) / BytesPerFrame;
        }
    }

    public float Volume
    {
        get
        {
            lock (_lock)
                return _stream is null ? 0 : SDL_GetAudioStreamGain(_stream);
        }
        set
        {
            lock (_lock)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_stream is not null)
                    SDL_SetAudioStreamGain(_stream, _volume);
            }
        }
    }

    public float Speed
    {
        get
        {
            lock (_lock)
                return _stream is null ? 1 : SDL_GetAudioStreamFrequencyRatio(_stream);
        }
        set
        {
            lock (_lock)
            {
                _speed = float.IsFinite(value) ? Math.Clamp(value, 0.01f, 100f) : 1f;
                // SDL resamples the stream on its way to the device, so it plays faster and higher, or slower and lower.
                if (_stream is not null)
                    SDL_SetAudioStreamFrequencyRatio(_stream, _speed);
            }
        }
    }

    public void Write(ReadOnlySpan<float> stereo)
    {
        lock (_lock)
        {
            if (_stream is null || stereo.IsEmpty)
                return;

            fixed (float* data = stereo)
            {
                if (!SDL_PutAudioStreamData(_stream, (IntPtr)data, stereo.Length * sizeof(float)))
                    throw new AudioDeviceException(SDL_GetError() ?? "Audio couldn't be queued.");
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_stream is not null)
                SDL_ClearAudioStream(_stream);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            if (_stream is not null)
                SDL_PauseAudioStreamDevice(_stream);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            if (_stream is not null)
                SDL_ResumeAudioStreamDevice(_stream);
        }

        // Off the caller's thread: reading the endpoint is a COM call.
        ThreadPool.QueueUserWorkItem(_ => CheckDevice());
    }

    public void Dispose()
    {
        _deviceCheck.Dispose();
        lock (_lock)
        {
            // A check still running can't open a stream on the device after this.
            _disposed = true;
            if (_stream is null)
                return;

            // Destroying a device stream closes its device too.
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }
    }

    /// <summary>Raises <see cref="DeviceChanged"/> when the stream is on another device than it was.</summary>
    private void CheckDevice()
    {
        // One check at a time, so the changes are raised in order; a check that finds one running skips.
        if (!_checkGate.TryEnter())
            return;
        try
        {
            FollowChosenDevice();

            if (ReadDevice() is not { } device)
                return;

            lock (_deviceLock)
            {
                if (device == _device)
                    return;
                _device = device;
            }
            DeviceChanged?.Invoke(device);
        }
        finally
        {
            _checkGate.Exit();
        }
    }

    /// <summary>Opens the chosen device, or the default one when there isn't one, it isn't connected, or it wouldn't open.</summary>
    private SDL_AudioStream* Open(SDL_AudioSpec* spec, string? outputDevice)
    {
        var candidate = string.IsNullOrWhiteSpace(outputDevice) ? null : AudioDevices.ResolveCandidate(outputDevice);
        if (candidate is { } chosen)
        {
            var stream = SDL_OpenAudioDeviceStream((SDL_AudioDeviceID)chosen.SdlId, spec, null, IntPtr.Zero);
            if (stream is not null)
            {
                _openedDeviceId = chosen.SdlId;
                _endpointId = chosen.EndpointId;
                return stream;
            }
            // The chosen device is there but won't open; the default one may still work.
        }

        return SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, spec, null, IntPtr.Zero);
    }

    /// <summary>
    /// Keeps the stream on the chosen device: moved to the default device when the chosen one is gone, and back to the
    /// chosen one once it is connected again. Does nothing when no device is chosen, as SDL follows the default device.
    /// </summary>
    private void FollowChosenDevice()
    {
        if (string.IsNullOrWhiteSpace(_preferredDevice))
            return;

        uint? opened;
        lock (_lock)
            opened = _openedDeviceId;

        if (opened is { } on)
        {
            if (!AudioDevices.IsConnected(on))
                Reopen(null);
        }
        else if (AudioDevices.ResolveCandidate(_preferredDevice) is { } returned)
        {
            Reopen(returned);
        }
    }

    /// <summary>
    /// Opens the stream again on <paramref name="target"/>, or on the default output device when that is null, and closes
    /// the old one; what was queued is dropped with it. When the chosen device won't open, the stream stays where it is.
    /// When the default device won't open, the stream stays closed and opening is tried again on the next check.
    /// </summary>
    private void Reopen(AudioDevices.Candidate? target)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = Channels, freq = SampleRate };
            var device = target is { } chosen ? (SDL_AudioDeviceID)chosen.SdlId : SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK;
            var stream = SDL_OpenAudioDeviceStream(device, &spec, null, IntPtr.Zero);
            if (stream is null && target is not null)
                return;

            if (_stream is not null)
            {
                // Left open, a stream on a disconnected device is a zombie that keeps swallowing audio.
                SDL_DestroyAudioStream(_stream);
                _stream = null;
            }

            if (stream is null)
                return;

            _stream = stream;
            // The default device moves with the system from here on; SDL migrates the stream by itself.
            _openedDeviceId = target?.SdlId;
            _endpointId = target?.EndpointId;
            _osLatency = null;
            SDL_SetAudioStreamGain(_stream, _volume);
            SDL_SetAudioStreamFrequencyRatio(_stream, _speed);
            // A stream opens paused.
            if (!_paused)
                SDL_ResumeAudioStreamDevice(_stream);
        }
    }

    /// <summary>The device the stream is on now, with the latency read for it, or null once it's closed.</summary>
    private AudioDevice? ReadDevice()
    {
        string? name;
        int sampleFrames = 0;
        int deviceRate = 0;
        lock (_lock)
        {
            if (_stream is null)
                return null;

            // The stream's device is the one it was opened on; for the default device SDL resolves it to the physical one.
            var device = SDL_GetAudioStreamDevice(_stream);
            name = SDL_GetAudioDeviceName(device);

            // SDL's device buffer, which the stream's queue doesn't count (C10 item 5).
            SDL_AudioSpec spec;
            int frames;
            if (SDL_GetAudioDeviceFormat(device, &spec, &frames))
            {
                sampleFrames = frames;
                deviceRate = spec.freq;
            }
        }
        name ??= "";
        if (!OperatingSystem.IsWindows())
            return new AudioDevice(name, null, DeviceLatency.Milliseconds(sampleFrames, deviceRate, null));

        // The endpoint's ID is cheap to read, so it's read every second; its latency isn't, so it's cached per
        // device. On a miss both are re-read from the same endpoint in one pass, so they can't come from different
        // devices when the default changes between reads. A chosen endpoint is read as itself, a default one follows
        // as it changes.
        var id = _endpointId ?? WindowsAudioEndpoint.DefaultRenderId();
        if (_osLatency is { } known && known.Name == name && known.Id == id)
            return new AudioDevice(name, id, DeviceLatency.Milliseconds(sampleFrames, deviceRate, known.Latency));

        var (freshId, freshLatency) = WindowsAudioEndpoint.RenderIdAndLatency(_endpointId, _lowLatencyAudio);
        _osLatency = _endpointId is null ? (name, freshId, freshLatency) : (name, _endpointId, freshLatency);
        return new AudioDevice(name, freshId ?? _endpointId, DeviceLatency.Milliseconds(sampleFrames, deviceRate, freshLatency));
    }
}
