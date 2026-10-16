using System.Runtime.InteropServices;

namespace ChartPacker.Thumbnails;

/// <summary>The HRESULTs, interface IDs and vtable calls the handler's COM objects share.</summary>
internal static unsafe class Com
{
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int EPointer = unchecked((int)0x80004003);
    public const int EFail = unchecked((int)0x80004005);
    public const int EUnexpected = unchecked((int)0x8000FFFF);
    public const int ClassENoAggregation = unchecked((int)0x80040110);
    public const int ClassEClassNotAvailable = unchecked((int)0x80040111);

    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IInitializeWithStream = new("B824B49D-22AC-4161-AC8A-9916E8FA3F7F");
    public static readonly Guid IThumbnailProvider = new("E357FCCD-A995-4576-B01F-234630154E96");

    // IUnknown: QueryInterface, AddRef, Release.
    private const int AddRefSlot = 1;
    private const int ReleaseSlot = 2;

    public static void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];

    public static void AddRef(IntPtr instance) =>
        ((delegate* unmanaged<IntPtr, uint>)Slot(instance, AddRefSlot))(instance);

    public static void Release(IntPtr instance) =>
        ((delegate* unmanaged<IntPtr, uint>)Slot(instance, ReleaseSlot))(instance);

    /// <summary>A vtable that lives as long as the process: the DLL is never unloaded (see <see cref="Exports"/>).</summary>
    public static void** AllocVtable(params ReadOnlySpan<IntPtr> slots)
    {
        var vtable = (void**)NativeMemory.Alloc((nuint)slots.Length, (nuint)sizeof(void*));
        for (int i = 0; i < slots.Length; i++)
            vtable[i] = (void*)slots[i];
        return vtable;
    }
}
