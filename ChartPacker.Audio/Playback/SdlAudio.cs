using SDL;
using static SDL.SDL3;

namespace ChartPacker.Audio.Playback;

/// <summary>
/// <see href="https://libsdl.org">SDL3</see>'s audio subsystem, started for as long as this lives,
/// and the outputs opened on it.
/// </summary>
/// <remarks>
/// SDL wants its subsystems started and stopped on the main thread only, so create and dispose
/// this there, once. <see cref="Open"/> may be called from any thread. When SDL can't be started,
/// e.g. because its native library is missing, that is kept and reported by <see cref="Open"/>
/// rather than thrown here, so the app still runs without sound.
/// </remarks>
public sealed class SdlAudio : IDisposable
{
    private readonly Lock _lock = new();
    private readonly string? _startError;
    private bool _started;

    /// <summary>Starts SDL's audio. Call on the main thread.</summary>
    public SdlAudio()
    {
        try
        {
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

    /// <summary>Opens the default output device at <paramref name="sampleRate"/>. Safe from any thread.</summary>
    /// <exception cref="AudioDeviceException">SDL's audio isn't running, or no output device could be opened.</exception>
    public IAudioOutput Open(int sampleRate)
    {
        lock (_lock)
        {
            if (!_started)
                throw new AudioDeviceException(_startError ?? "SDL audio has been stopped.");

            return new SdlAudioOutput(sampleRate);
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
