using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartPacker.Input.Midi;

/// <summary>The parts of Windows's winmm that read MIDI input ports.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Winmm
{
    public const uint CallbackFunction = 0x30000;
    public const uint MimOpen = 0x3C1;
    public const uint MimClose = 0x3C2;
    public const uint MimData = 0x3C3;
    public const uint NoError = 0;
    public const uint Allocated = 4;

    private const string Library = "winmm.dll";

    [LibraryImport(Library)]
    public static partial uint midiInGetNumDevs();

    [LibraryImport(Library, EntryPoint = "midiInGetDevCapsW")]
    public static partial uint midiInGetDevCaps(nuint deviceId, MidiInCaps* caps, uint size);

    [LibraryImport(Library)]
    public static partial uint midiInOpen(nint* handle, uint deviceId, nint callback, nint instance, uint flags);

    [LibraryImport(Library)]
    public static partial uint midiInStart(nint handle);

    [LibraryImport(Library)]
    public static partial uint midiInStop(nint handle);

    [LibraryImport(Library)]
    public static partial uint midiInReset(nint handle);

    [LibraryImport(Library)]
    public static partial uint midiInClose(nint handle);

    /// <summary><c>MIDIINCAPSW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MidiInCaps
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        public fixed char Name[32];
        public uint Support;
    }
}
