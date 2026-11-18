using SDL;
using static SDL.SDL3;

namespace ChartShared.Audio.Playback;

/// <summary>
/// An output device audio can be played on, offered as a choice in the apps' settings.
/// </summary>
/// <param name="Identifier">
/// What the setting stores, so the choice survives restarts: on Windows, the endpoint's ID (stable across runs,
/// renames and reboots); elsewhere, the device's name. An empty identifier stands for the default output device.
/// </param>
/// <param name="Name">The device's name, for showing in the list.</param>
/// <remarks>
/// The identifier is matched back to the device by looking it up at the time audio is opened, so a device that
/// isn't connected when the app starts (or when the chooser is shown) is reported as missing, never as the wrong
/// device.
/// </remarks>
public sealed record OutputDeviceInfo(string Identifier, string Name);

/// <summary>
/// The output devices SDL can see, as <see cref="OutputDeviceInfo"/>s. Enumeration is cheap enough to make the
/// choice list when the settings window opens; it isn't cached beyond that, so a device plugged in since the
/// list was made shows up the next time.
/// </summary>
/// <remarks>
/// On Windows a device is identified by its endpoint ID, read through the MMDevice API and matched to SDL's
/// device by its friendly name (SDL names its devices with the friendly name). Devices that share a friendly name
/// are paired in the order each side lists them, which is the best that can be done: SDL gives no ID to tell them
/// apart. A device whose SDL name has no endpoint of that name isn't offered. Elsewhere, SDL gives no stable
/// identifier this build can read, so the device's name is used.
/// </remarks>
public static class AudioDevices
{
    /// <summary>The buffer size SDL is asked of the device in low-latency mode, in sample frames: about 2.7 ms at 48 kHz.</summary>
    internal const int LowLatencySampleFrames = 128;

    /// <summary>Every connected output device, in no particular order. Empty when SDL can't be asked.</summary>
    public static IReadOnlyList<OutputDeviceInfo> Playback()
    {
        var candidates = Enumerate();
        var devices = new List<OutputDeviceInfo>(candidates.Count);
        foreach (var candidate in candidates)
            devices.Add(candidate.Info);

        return devices;
    }

    /// <summary>
    /// The device <paramref name="saved"/> identifies, from <paramref name="available"/>, or null when there isn't
    /// one — a null or empty <paramref name="saved"/>, or a device that isn't in the list anymore. The caller then
    /// opens the default output device.
    /// </summary>
    public static OutputDeviceInfo? Resolve(string? saved, IReadOnlyList<OutputDeviceInfo> available)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return null;

        foreach (var device in available)
        {
            if (string.Equals(device.Identifier, saved, StringComparison.OrdinalIgnoreCase))
                return device;
        }

        return null;
    }

    /// <summary>The device <paramref name="saved"/> identifies, with the SDL ID needed to open it, or null when it isn't connected.</summary>
    internal static Candidate? ResolveCandidate(string? saved)
    {
        var candidates = Enumerate();
        var found = Resolve(saved, [.. candidates.Select(candidate => candidate.Info)]);
        if (found is null)
            return null;

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate.Info, found))
                return candidate;
        }

        return null;
    }

    /// <summary>Whether the physical device <paramref name="deviceId"/> is still connected.</summary>
    internal static bool IsConnected(uint deviceId)
    {
        foreach (var devid in SdlPlaybackDevices())
        {
            if ((uint)devid == deviceId)
                return true;
        }

        return false;
    }

    /// <summary>Every connected playback device with the SDL ID needed to open it.</summary>
    internal static IReadOnlyList<Candidate> Enumerate()
    {
        var candidates = new List<Candidate>();
        if (OperatingSystem.IsWindows())
        {
            var endpoints = WindowsAudioEndpoint.RenderEndpoints();
            if (endpoints.Count == 0)
                return candidates;

            var devices = new List<(uint SdlId, string? Name)>();
            foreach (var devid in SdlPlaybackDevices())
                devices.Add(((uint)devid, SDL_GetAudioDeviceName(devid)));

            return Pair(endpoints, devices);
        }

        foreach (var devid in SdlPlaybackDevices())
        {
            var name = SDL_GetAudioDeviceName(devid);
            if (string.IsNullOrEmpty(name))
                continue;

            candidates.Add(new Candidate(new OutputDeviceInfo(name, name), (uint)devid));
        }

        return candidates;
    }

    /// <summary>
    /// Matches each SDL device to the Windows endpoint with its name, each endpoint to at most one device. Endpoints
    /// that share a name are handed out in the order given, so two identical devices don't both get the first's ID.
    /// A device with no endpoint of its name is left out.
    /// </summary>
    internal static List<Candidate> Pair(IReadOnlyList<(string Id, string Name)> endpoints, IReadOnlyList<(uint SdlId, string? Name)> devices)
    {
        var byName = new Dictionary<string, Queue<(string Id, string Name)>>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            if (!byName.TryGetValue(endpoint.Name, out var queue))
                byName[endpoint.Name] = queue = new Queue<(string, string)>();
            queue.Enqueue(endpoint);
        }

        var candidates = new List<Candidate>(devices.Count);
        foreach (var (sdlId, name) in devices)
        {
            if (name is null || !byName.TryGetValue(name, out var queue) || !queue.TryDequeue(out var endpoint))
                continue;

            candidates.Add(new Candidate(new OutputDeviceInfo(endpoint.Id, endpoint.Name), sdlId));
        }

        return candidates;
    }

    private static List<SDL_AudioDeviceID> SdlPlaybackDevices()
    {
        var devices = new List<SDL_AudioDeviceID>();
        using var array = SDL_GetAudioPlaybackDevices();
        if (array is null)
            return devices;

        foreach (var device in array)
            devices.Add(device);

        return devices;
    }

    /// <summary>A device, with the SDL ID needed to open it.</summary>
    internal readonly record struct Candidate(OutputDeviceInfo Info, uint SdlId)
    {
        /// <summary>The Windows endpoint's ID, which is the identifier there; none elsewhere.</summary>
        public string? EndpointId => OperatingSystem.IsWindows() ? Info.Identifier : null;
    }
}
