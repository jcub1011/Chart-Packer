using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartShared.Audio.Playback;

/// <summary>
/// The ID of Windows' default output endpoint, from the MMDevice API. SDL follows that endpoint, and
/// its ID (unlike SDL's device name) stays the same across reboots and renames, so the calibrated
/// latency is kept under it (C11).
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

    // IUnknown: QueryInterface, AddRef, Release.
    private const int ReleaseSlot = 2;
    // IMMDeviceEnumerator: EnumAudioEndpoints, GetDefaultAudioEndpoint, ...
    private const int GetDefaultAudioEndpointSlot = 4;
    // IMMDevice: Activate, OpenPropertyStore, GetId, GetState.
    private const int GetIdSlot = 5;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    /// <summary>The default render endpoint's ID, or null when there isn't one or it can't be read.</summary>
    public static string? DefaultRenderId()
    {
        try
        {
            var init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
            // A thread already in a single-threaded apartment (the UI thread) can still make these calls.
            if (init < 0 && init != RpcEChangedMode)
                return null;
            try
            {
                return ReadDefaultRenderId();
            }
            finally
            {
                if (init >= 0)
                    CoUninitialize();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadDefaultRenderId()
    {
        var clsid = MMDeviceEnumeratorClsid;
        var iid = IMMDeviceEnumeratorIid;
        if (CoCreateInstance(&clsid, IntPtr.Zero, ClsctxAll, &iid, out var enumerator) < 0 || enumerator == IntPtr.Zero)
            return null;

        try
        {
            IntPtr device;
            var getDefault = (delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, GetDefaultAudioEndpointSlot);
            // Fails with E_NOTFOUND when there is no output device at all.
            if (getDefault(enumerator, ERender, EConsole, &device) < 0 || device == IntPtr.Zero)
                return null;

            try
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
