using System.Runtime.InteropServices;

namespace ChartPacker.Thumbnails;

/// <summary>
/// The DLL's exports, which make it an in-process COM server, and its one class factory. The installer
/// registers <see cref="SngThumbnailProvider.Clsid"/> against the DLL and as <c>.sng</c>'s thumbnail handler.
/// </summary>
/// <remarks>
/// A Native AOT library can't be unloaded, so <c>DllCanUnloadNow</c> always says no, and the factory and the
/// vtables are made once and never freed. The host process keeps the file in use as a result, so the installer
/// ends it before replacing or removing the DLL. Nothing may throw back into the caller, so every entry point catches.
/// </remarks>
internal static unsafe class Exports
{
    // The factory is a static object: a pointer to its vtable. It isn't counted, since it's never freed.
    private static readonly IntPtr Factory = CreateFactory();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    private static int DllGetClassObject(Guid* clsid, Guid* iid, void** result)
    {
        if (result is null)
            return Com.EPointer;
        *result = null;

        try
        {
            return *clsid == SngThumbnailProvider.Clsid ? QueryFactory(Factory, iid, result) : Com.ClassEClassNotAvailable;
        }
        catch
        {
            return Com.EUnexpected;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    private static int DllCanUnloadNow() => Com.SFalse;

    private static IntPtr CreateFactory()
    {
        void** vtable = Com.AllocVtable(
            (IntPtr)(delegate* unmanaged<IntPtr, Guid*, void**, int>)&FactoryQueryInterface,
            (IntPtr)(delegate* unmanaged<IntPtr, uint>)&FactoryAddRef,
            (IntPtr)(delegate* unmanaged<IntPtr, uint>)&FactoryRelease,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, Guid*, void**, int>)&CreateInstance,
            (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&LockServer);

        var factory = (void***)NativeMemory.Alloc((nuint)sizeof(void*));
        *factory = vtable;
        return (IntPtr)factory;
    }

    private static int QueryFactory(IntPtr self, Guid* iid, void** result)
    {
        if (*iid == Com.IUnknown || *iid == Com.IClassFactory)
        {
            *result = (void*)self;
            return Com.SOk;
        }

        *result = null;
        return Com.ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static int FactoryQueryInterface(IntPtr self, Guid* iid, void** result) =>
        result is null ? Com.EPointer : QueryFactory(self, iid, result);

    [UnmanagedCallersOnly]
    private static uint FactoryAddRef(IntPtr self) => 2;

    [UnmanagedCallersOnly]
    private static uint FactoryRelease(IntPtr self) => 1;

    [UnmanagedCallersOnly]
    private static int CreateInstance(IntPtr self, IntPtr outer, Guid* iid, void** result)
    {
        if (result is null)
            return Com.EPointer;
        *result = null;
        if (outer != IntPtr.Zero)
            return Com.ClassENoAggregation;

        try
        {
            return SngThumbnailProvider.Create(iid, result);
        }
        catch
        {
            return Com.EUnexpected;
        }
    }

    [UnmanagedCallersOnly]
    private static int LockServer(IntPtr self, int locking) => Com.SOk;
}
