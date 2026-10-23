using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartShared.Audio.Playback;

/// <summary>
/// Windows' default output endpoint, from the MMDevice API: its ID, and its latency as WASAPI reports it.
/// SDL follows that endpoint, and its ID (unlike SDL's device name) stays the same across reboots and
/// renames, so the calibrated latency is kept under it (C11).
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
    private const int AudclntSharemodeShared = 0;

    // IUnknown: QueryInterface, AddRef, Release.
    private const int ReleaseSlot = 2;
    // IMMDeviceEnumerator: EnumAudioEndpoints, GetDefaultAudioEndpoint, ...
    private const int GetDefaultAudioEndpointSlot = 4;
    // IMMDevice: Activate, OpenPropertyStore, GetId, GetState.
    private const int ActivateSlot = 3;
    private const int GetIdSlot = 5;
    // IAudioClient: Initialize, GetBufferSize, GetStreamLatency, GetCurrentPadding, IsFormatSupported, GetMixFormat,
    // GetDevicePeriod, ...
    private const int InitializeSlot = 3;
    private const int GetStreamLatencySlot = 5;
    private const int GetMixFormatSlot = 8;
    private const int GetDevicePeriodSlot = 9;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    /// <summary>The default render endpoint's ID, or null when there isn't one or it can't be read.</summary>
    public static string? DefaultRenderId() => OnDefaultRender(ReadId);

    /// <summary>
    /// What the default render endpoint adds after SDL's buffer, as WASAPI reports it for a shared-mode stream such as
    /// SDL's: the stream's latency and the device's period. Null when there isn't one or it can't be read.
    /// </summary>
    /// <remarks>
    /// SDL's own stream can't be asked, so a stream of the same kind is opened on the endpoint, asked, and let go
    /// without ever being started. It can't see a Bluetooth link's delay, nor the display's (issue #140).
    /// </remarks>
    public static TimeSpan? DefaultRenderLatency() => OnDefaultRender(ReadLatency);

    /// <summary>
    /// The default render endpoint's ID and latency, read from the same endpoint in one pass, so the two can't come
    /// from different devices when the default changes between reads. Either may be null when it can't be read.
    /// </summary>
    public static (string? Id, TimeSpan? Latency) DefaultRenderIdAndLatency() =>
        OnDefaultRender(ReadIdAndLatency);

    // What read gives for the default render endpoint, or null when there isn't one, or COM can't be used.
    private static T? OnDefaultRender<T>(Func<IntPtr, T?> read)
    {
        try
        {
            var init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            // A thread already in a single-threaded apartment (the UI thread) can still make these calls.
            if (init < 0 && init != RpcEChangedMode)
                return default;
            try
            {
                return WithDefaultRender(read);
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

    private static T? WithDefaultRender<T>(Func<IntPtr, T?> read)
    {
        var clsid = MMDeviceEnumeratorClsid;
        var iid = IMMDeviceEnumeratorIid;
        if (CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iid, out var enumerator) < 0 || enumerator == IntPtr.Zero)
            return default;

        try
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
        finally
        {
            Release(enumerator);
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

    private static (string? Id, TimeSpan? Latency) ReadIdAndLatency(IntPtr device) =>
        (ReadId(device), ReadLatency(device));

    private static TimeSpan? ReadLatency(IntPtr device)
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
                // Shared, with no flags and the engine's own buffer and period, as SDL opens it. The stream's latency is
                // only known once it's initialized.
                var initialize = (delegate* unmanaged<IntPtr, int, uint, long, long, IntPtr, Guid*, int>)Slot(client, InitializeSlot);
                if (initialize(client, AudclntSharemodeShared, 0, 0, 0, format, null) < 0)
                    return null;
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

    private static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    private static void Release(IntPtr instance) =>
        ((delegate* unmanaged<IntPtr, uint>)Slot(instance, ReleaseSlot))(instance);

    [LibraryImport("ole32")]
    private static partial int CoInitializeEx(IntPtr reserved, int coInit);

    [LibraryImport("ole32")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32")]
    private static partial int CoCreateInstance(Guid* clsid, IntPtr outer, int context, Guid* iid, out IntPtr instance);

    [LibraryImport("ole32")]
    private static partial void CoTaskMemFree(IntPtr memory);
}
