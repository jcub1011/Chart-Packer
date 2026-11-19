using SDL;
using static SDL.SDL3;

namespace ChartShared.Audio.Playback;

/// <summary>
/// <see href="https://libsdl.org">SDL3</see>'s audio subsystem, started for as long as this lives,
/// and the outputs opened on it.
/// </summary>
/// <remarks>
/// SDL wants its subsystems started and stopped on the main thread only, so create and dispose
/// this there, once. <see cref="Open"/> may be called from any thread. When SDL can't be started,
/// e.g. because its native library is missing, that is kept and reported by <see cref="Open"/>
/// rather than thrown here, so the app still runs without sound.
/// <para>
/// On Windows, the audio device is opened with a small buffer (a few milliseconds) when <paramref name="lowLatencyAudio"/>
/// is on, which is what makes WASAPI use its low-latency path. SDL assigns a device's buffer size when it enumerates the
/// device, which happens as the audio subsystem starts, so the setting applies when SDL starts, not when a device is opened:
/// change it between runs, not while one is playing.
/// </para>
/// <para>
/// The output opens the device <paramref name="outputDevice"/> reports when it is opened, or the default output device when
/// there isn't one or it isn't connected. It's read anew each time <see cref="Open"/> is called, so a change applies to
/// audio opened after it.
/// </para>
/// </remarks>
public sealed class SdlAudio : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Func<string?>? _outputDevice;
    private readonly bool _lowLatencyAudio;
    private readonly string? _startError;
    private bool _started;

    /// <summary>
    /// Starts SDL's audio. Call on the main thread. <paramref name="outputDevice"/> reports the output device to open,
    /// each time one is: on Windows its endpoint ID, elsewhere its SDL name; null or empty for the default device.
    /// </summary>
    public SdlAudio(bool lowLatencyAudio = true, Func<string?>? outputDevice = null)
    {
        _outputDevice = outputDevice;
        _lowLatencyAudio = lowLatencyAudio;
        try
        {
            if (OperatingSystem.IsWindows() && lowLatencyAudio)
                SDL_SetHint("SDL_AUDIO_DEVICE_SAMPLE_FRAMES", AudioDevices.LowLatencySampleFrames.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
                _started = true;
            else
                _startError = SDL_GetError() ?? "SDL audio couldn't be started.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            _startError = ex.Message;
        }
    }

    /// <summary>Opens the selected output device at <paramref name="sampleRate"/>, or the default device when there isn't one. Safe from any thread.</summary>
    /// <exception cref="AudioDeviceException">SDL's audio isn't running, or no output device could be opened.</exception>
    public IAudioOutput Open(int sampleRate)
    {
        lock (_lock)
        {
            if (!_started)
                throw new AudioDeviceException(_startError ?? "SDL audio has been stopped.");

            return new SdlAudioOutput(sampleRate, _outputDevice?.Invoke(), _lowLatencyAudio);
        }
    }

    /// <summary>Stops SDL's audio. Call on the main thread, after every output is disposed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_started)
                return;

            _started = false;
            SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
        }
    }
}
