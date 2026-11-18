using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartShared.Audio.Playback;

/// <summary>
/// Windows' render endpoints, from the MMDevice API: their IDs, friendly names and latencies as WASAPI reports them.
/// SDL follows the default endpoint, and its ID (unlike SDL's device name) stays the same across reboots and
/// renames, so the calibrated latency is kept under it (C11). The endpoints can also be enumerated, and read by ID,
/// for the output device the app is asked to play on.
/// </summary>
/// <remarks>
/// The COM calls go through the interfaces' vtables by hand rather than through COM interop, which
/// keeps this trim- and AOT-safe.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsAudioEndpoint
{
    private const int ClsctxAll = 0x17;
    private const int CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ERender = 0;
    private const int EConsole = 0;
    private const int StgmRead = 0;
    private const int AudclntSharemodeShared = 0;
    private const ushort VtLpwstr = 31;

    // IUnknown: QueryInterface, AddRef, Release.
    private const int ReleaseSlot = 2;
    // IMMDeviceEnumerator: EnumAudioEndpoints, GetDefaultAudioEndpoint, ...
    private const int EnumAudioEndpointsSlot = 3;
    private const int GetDefaultAudioEndpointSlot = 4;
    // IMMDeviceCollection: GetCount, Item, ...
    private const int CollectionGetCountSlot = 3;
    private const int CollectionItemSlot = 4;
    // IMMDevice: Activate, OpenPropertyStore, GetId, GetState.
    private const int ActivateSlot = 3;
    private const int OpenPropertyStoreSlot = 4;
    private const int GetIdSlot = 5;
    // IPropertyStore: GetValue, ...
    private const int PropertyStoreGetValueSlot = 5;
    // IAudioClient: Initialize, GetBufferSize, GetStreamLatency, GetCurrentPadding, IsFormatSupported, GetMixFormat,
    // GetDevicePeriod, ...
    private const int InitializeSlot = 3;
    private const int GetStreamLatencySlot = 5;
    private const int GetMixFormatSlot = 8;
    private const int GetDevicePeriodSlot = 9;
    // IAudioClient2: IsOffloadCapable, SetClientProperties, GetBufferSizeLimits;
    // IAudioClient3 follows: GetSharedModeEnginePeriod, GetCurrentSharedModeEnginePeriod, InitializeSharedAudioStream.
    private const int GetSharedModeEnginePeriodSlot = 18;
    private const int InitializeSharedAudioStreamSlot = 20;

    // IAudioClient3: the small-period shared stream, opened the way SDL opens it.
    private const uint StreamFlagsEventCallback = 0x00040000;
    // IAudioClient3: the small-period shared stream, as Windows documents it for low-latency audio.
    private static readonly Guid IAudioClient3Iid = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    // The buffer SDL asks of the device in low-latency mode, in sample frames: about 2.7 ms at 48 kHz.
    private const int LowLatencySampleFrames = 128;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    // PKEY_Device_FriendlyName: what Windows shows for the endpoint; SDL names its devices with it.
    private static readonly Guid FriendlyNameFormatId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    private const int FriendlyNamePid = 14;
    // DEVICE_STATE_ACTIVE: only endpoints that can play.
    private const int DeviceStateActive = 1;

    /// <summary>The default render endpoint's ID, or null when there isn't one or it can't be read.</summary>
    public static string? DefaultRenderId() => OnDefaultRender(ReadId);

    // What read gives for the default render endpoint, or null when there isn't one, or COM can't be used.
    private static T? OnDefaultRender<T>(Func<IntPtr, T?> read) =>
        OnEndpoints(enumerator => WithDefaultRender(enumerator, read));

    /// <summary>
    /// What <paramref name="endpointId"/> names: its ID and latency, read from the same endpoint in one pass, so the two
    /// can't come from different devices. A null or empty ID reads the default render endpoint, like <see cref="DefaultRenderIdAndLatency"/>.
    /// Either of the pair may be null when it can't be read; both are null when the endpoint is gone.
    /// </summary>
    /// <remarks>
    /// <paramref name="lowLatency"/> should say whether SDL was asked to open the device with its small buffer, so the probe
    /// opens its stream the same way, with the same periodicity, as the stream WASAPI actually plays.
    /// </remarks>
    public static (string? Id, TimeSpan? Latency) RenderIdAndLatency(string? endpointId, bool lowLatency = false)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return DefaultRenderIdAndLatency(lowLatency);

        var read = OnEndpoints(enumerator => WithEndpoint(enumerator, endpointId, (device) => ReadIdAndLatency(device, lowLatency)));
        return read is { } value ? (value.Id, value.Latency) : (null, null);
    }

    /// <summary>
    /// Every active render endpoint's ID and friendly name, in the order the MMDevice enumerator returns them. Empty
    /// when there isn't one or COM can't be used.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> RenderEndpoints() =>
        OnEndpoints(EnumerateEndpoints) ?? [];

    /// <summary>
    /// What the default render endpoint adds after SDL's buffer, as WASAPI reports it for a shared-mode stream such as
    /// SDL's: the stream's latency and the device's period. Null when there isn't one or it can't be read.
    /// </summary>
    /// <remarks>
    /// SDL's own stream can't be asked, so a stream of the same kind is opened on the endpoint, asked, and let go
    /// without ever being started. It can't see a Bluetooth link's delay, nor the display's (issue #140).
    /// <paramref name="lowLatency"/> should say whether SDL was asked to open the device with its small buffer, so the
    /// probe opens its stream the same way.
    /// </remarks>
    public static TimeSpan? DefaultRenderLatency(bool lowLatency = false) => OnDefaultRender(device => ReadLatency(device, lowLatency));

    /// <summary>
    /// The default render endpoint's ID and latency, read from the same endpoint in one pass, so the two can't come
    /// from different devices when the default changes between reads. Either may be null when it can't be read.
    /// </summary>
    public static (string? Id, TimeSpan? Latency) DefaultRenderIdAndLatency(bool lowLatency = false) =>
        OnDefaultRender(device => ReadIdAndLatency(device, lowLatency));

    // What read gives for an endpoint reached through the MMDevice enumerator, or null when COM can't be used.
    private static T? OnEndpoints<T>(Func<IntPtr, T?> read)
    {
        try
        {
            var init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            // A thread already in a single-threaded apartment (the UI thread) can still make these calls.
            if (init < 0 && init != RpcEChangedMode)
                return default;
            try
            {
                var clsid = MMDeviceEnumeratorClsid;
                var iid = IMMDeviceEnumeratorIid;
                if (CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iid, out var enumerator) < 0 || enumerator == IntPtr.Zero)
                    return default;

                try
                {
                    return read(enumerator);
                }
                finally
                {
                    Release(enumerator);
                }
            }
            finally
            {
                if (init >= 0)
                    CoUninitialize();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return default;
        }
    }

    private static T? WithDefaultRender<T>(IntPtr enumerator, Func<IntPtr, T?> read)
    {
        IntPtr device;
        var getDefault = (delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, GetDefaultAudioEndpointSlot);
        // Fails with E_NOTFOUND when there is no output device at all.
        if (getDefault(enumerator, ERender, EConsole, &device) < 0 || device == IntPtr.Zero)
            return default;

        try
        {
            return read(device);
        }
        finally
        {
            Release(device);
        }
    }

    private static T? WithEndpoint<T>(IntPtr enumerator, string endpointId, Func<IntPtr, T?> read)
    {
        var collection = EnumEndpoints(enumerator);
        if (collection == IntPtr.Zero)
            return default;

        try
        {
            var count = CollectionCount(collection);
            for (int i = 0; i < count; i++)
            {
                var device = CollectionItem(collection, i);
                if (device == IntPtr.Zero)
                    continue;

                try
                {
                    var id = ReadId(device);
                    if (id is not null && string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase))
                        return read(device);
                }
                finally
                {
                    Release(device);
                }
            }

            return default;
        }
        finally
        {
            Release(collection);
        }
    }

    private static List<(string Id, string Name)>? EnumerateEndpoints(IntPtr enumerator)
    {
        var collection = EnumEndpoints(enumerator);
        if (collection == IntPtr.Zero)
            return null;

        try
        {
            var devices = new List<(string, string)>();
            var count = CollectionCount(collection);
            for (int i = 0; i < count; i++)
            {
                var device = CollectionItem(collection, i);
                if (device == IntPtr.Zero)
                    continue;

                try
                {
                    var id = ReadId(device);
                    var name = ReadFriendlyName(device);
                    if (id is not null && name is not null)
                        devices.Add((id, name));
                }
                finally
                {
                    Release(device);
                }
            }

            return devices;
        }
        finally
        {
            Release(collection);
        }
    }

    private static IntPtr EnumEndpoints(IntPtr enumerator)
    {
        var enumEndpoints = (delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, EnumAudioEndpointsSlot);
        IntPtr collection;
        // Active render endpoints only; disconnected ones can't play.
        if (enumEndpoints(enumerator, ERender, DeviceStateActive, &collection) < 0)
            return IntPtr.Zero;

        return collection;
    }

    private static int CollectionCount(IntPtr collection)
    {
        var getCount = (delegate* unmanaged<IntPtr, int*, int>)Slot(collection, CollectionGetCountSlot);
        int count;
        return getCount(collection, &count) < 0 ? 0 : count;
    }

    private static IntPtr CollectionItem(IntPtr collection, int index)
    {
        var item = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(collection, CollectionItemSlot);
        IntPtr device;
        return item(collection, index, &device) < 0 ? IntPtr.Zero : device;
    }

    private static string? ReadFriendlyName(IntPtr device)
    {
        var openStore = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)Slot(device, OpenPropertyStoreSlot);
        IntPtr store;
        if (openStore(device, StgmRead, &store) < 0 || store == IntPtr.Zero)
            return null;

        try
        {
            var key = new PropertyKey { FormatId = FriendlyNameFormatId, Pid = FriendlyNamePid };
            PropVariant value = default;
            var getValue = (delegate* unmanaged<IntPtr, PropertyKey*, PropVariant*, int>)Slot(store, PropertyStoreGetValueSlot);
            if (getValue(store, &key, &value) < 0)
                return null;

            try
            {
                return value.Vt == VtLpwstr && value.Data1 != IntPtr.Zero ? new string((char*)value.Data1) : null;
            }
            finally
            {
                PropVariantClear(&value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private static string? ReadId(IntPtr device)
    {
        char* id;
        var getId = (delegate* unmanaged<IntPtr, char**, int>)Slot(device, GetIdSlot);
        if (getId(device, &id) < 0 || id is null)
            return null;

        try
        {
            return new string(id);
        }
        finally
        {
            CoTaskMemFree((IntPtr)id);
        }
    }

    private static (string? Id, TimeSpan? Latency) ReadIdAndLatency(IntPtr device, bool lowLatency) =>
        (ReadId(device), ReadLatency(device, lowLatency));

    private static TimeSpan? ReadLatency(IntPtr device, bool lowLatency)
    {
        var iid = IAudioClientIid;
        IntPtr client;
        var activate = (delegate* unmanaged<IntPtr, Guid*, int, IntPtr, IntPtr*, int>)Slot(device, ActivateSlot);
        if (activate(device, &iid, ClsctxAll, IntPtr.Zero, &client) < 0 || client == IntPtr.Zero)
            return null;

        try
        {
            IntPtr format;
            var getMixFormat = (delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(client, GetMixFormatSlot);
            if (getMixFormat(client, &format) < 0 || format == IntPtr.Zero)
                return null;

            try
            {
                // Open a stream of the same kind SDL opens, so the latency asked for is the latency heard: a shared stream
                // with a small periodicity via IAudioClient3 (the low-latency path), with SDL's own requested buffer size.
                // The stream's latency is only known once it's initialized.
                if (!TryInitializeLowPeriod(client, format, lowLatency))
                {
                    var initialize = (delegate* unmanaged<IntPtr, int, uint, long, long, IntPtr, Guid*, int>)Slot(client, InitializeSlot);
                    if (initialize(client, AudclntSharemodeShared, 0, 0, 0, format, null) < 0)
                        return null;
                }
            }
            finally
            {
                CoTaskMemFree(format);
            }

            // Both in 100 ns units, as TimeSpan's ticks are.
            long stream, period, minimum;
            var getStreamLatency = (delegate* unmanaged<IntPtr, long*, int>)Slot(client, GetStreamLatencySlot);
            var getDevicePeriod = (delegate* unmanaged<IntPtr, long*, long*, int>)Slot(client, GetDevicePeriodSlot);
            if (getStreamLatency(client, &stream) < 0 || getDevicePeriod(client, &period, &minimum) < 0)
                return null;

            return TimeSpan.FromTicks(Math.Max(0, stream) + Math.Max(0, period));
        }
        finally
        {
            Release(client);
        }
    }

    /// <summary>
    /// The buffer SDL aims for, in frames: its low-latency request when <paramref name="lowLatency"/> is on, otherwise the
    /// frequency-based default SDL uses (<c>SDL_GetDefaultSampleFramesFromFreq</c>).
    /// </summary>
    private static int SampleFramesFor(int freq, bool lowLatency)
    {
        if (lowLatency)
            return AudioDevices.LowLatencySampleFrames;
        if (freq <= 22050) return 512;
        if (freq <= 48000) return 1024;
        if (freq <= 96000) return 2048;
        return 4096;
    }

    /// <summary>
    /// Initializes <paramref name="client"/> with IAudioClient3 at SDL's requested periodicity, as SDL does when the
    /// engine supports it: an integral multiple of the fundamental period, clamped to the engine's range. False when
    /// IAudioClient3 can't be used, and the caller should fall back to a plain shared initialization.
    /// </summary>
    private static bool TryInitializeLowPeriod(IntPtr client, IntPtr format, bool lowLatency)
    {
        var iid = IAudioClient3Iid;
        var queryInterface = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(client, 0);
        IntPtr client3;
        if (queryInterface(client, &iid, &client3) < 0 || client3 == IntPtr.Zero)
            return false;

        try
        {
            // WAVEFORMATEX: wFormatTag (2), nChannels (2), nSamplesPerSec (4). The engine's format has that layout too,
            // WAVEFORMATEXTENSIBLE included.
            var freq = ((int*)((byte*)format + 4))[0];
            var requested = SampleFramesFor(freq > 0 ? freq : 48000, lowLatency);

            uint defaultPeriod, fundamental, minPeriod, maxPeriod;
            var getPeriod = (delegate* unmanaged<IntPtr, IntPtr, uint*, uint*, uint*, uint*, int>)Slot(client3, GetSharedModeEnginePeriodSlot);
            if (getPeriod(client3, format, &defaultPeriod, &fundamental, &minPeriod, &maxPeriod) < 0)
                return false;
            if (fundamental == 0)
                return false;

            var period = fundamental * (uint)Math.Round((double)requested / fundamental);
            period = Math.Clamp(period, minPeriod, maxPeriod);

            var initialize = (delegate* unmanaged<IntPtr, uint, uint, IntPtr, Guid*, int>)Slot(client3, InitializeSharedAudioStreamSlot);
            // Event callback and no session GUID, as SDL opens it; the stream is never started.
            return initialize(client3, StreamFlagsEventCallback, period, format, null) >= 0;
        }
        finally
        {
            Release(client3);
        }
    }

    private static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    private static void Release(IntPtr instance) =>
        ((delegate* unmanaged<IntPtr, uint>)Slot(instance, ReleaseSlot))(instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint Pid;
    }

    // VT_LPWSTR lives in the union at its start on 64-bit; only that kind is read.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(8)]
        public IntPtr Data1;
    }

    [LibraryImport("ole32")]
    private static partial int CoInitializeEx(IntPtr reserved, int coInit);

    [LibraryImport("ole32")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32")]
    private static partial int CoCreateInstance(Guid* clsid, IntPtr outer, int context, Guid* iid, out IntPtr instance);

    [LibraryImport("ole32")]
    private static partial void CoTaskMemFree(IntPtr memory);

    [LibraryImport("ole32")]
    private static partial int PropVariantClear(PropVariant* pvar);
}
