using System.Runtime.InteropServices;
using ChartShared.Core.Scanning;

namespace ChartPacker.Thumbnails;

/// <summary>
/// The <c>.sng</c> thumbnail handler: a COM object with <c>IInitializeWithStream</c> and <c>IThumbnailProvider</c>.
/// Explorer hands it the file as a stream, and it returns the song's album art scaled to the size asked for.
/// </summary>
/// <remarks>
/// A song without art, or one that can't be read, fails <c>GetThumbnail</c>; Explorer then shows the <c>.sng</c>
/// type's icon, the disc. Explorer runs thumbnail handlers in a separate host process, so a bad file can't take
/// Explorer down with it.
/// </remarks>
internal static unsafe class SngThumbnailProvider
{
    /// <summary>The handler's class ID. The installer registers it; never change it.</summary>
    public static readonly Guid Clsid = new("227CBBE3-F529-4DBC-969C-AD8C87203253");

    private const int WtsatArgb = 2;
    // HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED)
    private const int EAlreadyInitialized = unchecked((int)0x800704DF);

    // The object, in native memory: one vtable pointer per interface, then its state. IInitializeWithStream's is
    // first, so it doubles as the object's IUnknown; IThumbnailProvider's methods step back one pointer to find it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public void** InitializeVtable;
        public void** ThumbnailVtable;
        public int RefCount;
        public IntPtr Stream;
    }

    private static readonly void** InitializeVtable = Com.AllocVtable(
        (IntPtr)(delegate* unmanaged<Instance*, Guid*, void**, int>)&QueryInterface,
        (IntPtr)(delegate* unmanaged<Instance*, uint>)&AddRef,
        (IntPtr)(delegate* unmanaged<Instance*, uint>)&Release,
        (IntPtr)(delegate* unmanaged<Instance*, IntPtr, uint, int>)&Initialize);

    private static readonly void** ThumbnailVtable = Com.AllocVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, Guid*, void**, int>)&ThumbnailQueryInterface,
        (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ThumbnailAddRef,
        (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ThumbnailRelease,
        (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, int*, int>)&GetThumbnail);

    /// <summary>Makes a handler and returns its <paramref name="iid"/> interface, as <c>IClassFactory::CreateInstance</c> does.</summary>
    public static int Create(Guid* iid, void** result)
    {
        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        instance->InitializeVtable = InitializeVtable;
        instance->ThumbnailVtable = ThumbnailVtable;
        instance->RefCount = 1;

        // The query adds the caller's reference; dropping the one made here frees the object if the query failed.
        int hr = Query(instance, iid, result);
        Unref(instance);
        return hr;
    }

    private static int Query(Instance* instance, Guid* iid, void** result)
    {
        if (*iid == Com.IUnknown || *iid == Com.IInitializeWithStream)
            *result = &instance->InitializeVtable;
        else if (*iid == Com.IThumbnailProvider)
            *result = &instance->ThumbnailVtable;
        else
        {
            *result = null;
            return Com.ENoInterface;
        }

        Interlocked.Increment(ref instance->RefCount);
        return Com.SOk;
    }

    private static uint Unref(Instance* instance)
    {
        int count = Interlocked.Decrement(ref instance->RefCount);
        if (count == 0)
        {
            if (instance->Stream != IntPtr.Zero)
                Com.Release(instance->Stream);
            NativeMemory.Free(instance);
        }

        return (uint)count;
    }

    private static Instance* FromThumbnail(IntPtr self) => (Instance*)((byte*)self - sizeof(void*));

    [UnmanagedCallersOnly]
    private static int QueryInterface(Instance* self, Guid* iid, void** result) =>
        result is null ? Com.EPointer : Query(self, iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->RefCount);

    [UnmanagedCallersOnly]
    private static uint Release(Instance* self) => Unref(self);

    [UnmanagedCallersOnly]
    private static int ThumbnailQueryInterface(IntPtr self, Guid* iid, void** result) =>
        result is null ? Com.EPointer : Query(FromThumbnail(self), iid, result);

    [UnmanagedCallersOnly]
    private static uint ThumbnailAddRef(IntPtr self) => (uint)Interlocked.Increment(ref FromThumbnail(self)->RefCount);

    [UnmanagedCallersOnly]
    private static uint ThumbnailRelease(IntPtr self) => Unref(FromThumbnail(self));

    // IInitializeWithStream::Initialize. The stream is kept until the handler is released.
    [UnmanagedCallersOnly]
    private static int Initialize(Instance* self, IntPtr stream, uint mode)
    {
        if (stream == IntPtr.Zero)
            return Com.EPointer;
        if (self->Stream != IntPtr.Zero)
            return EAlreadyInitialized;

        Com.AddRef(stream);
        self->Stream = stream;
        return Com.SOk;
    }

    // IThumbnailProvider::GetThumbnail: the album art fitted into size × size.
    [UnmanagedCallersOnly]
    private static int GetThumbnail(IntPtr self, uint size, IntPtr* bitmap, int* alpha)
    {
        if (bitmap is null || alpha is null)
            return Com.EPointer;
        *bitmap = IntPtr.Zero;
        *alpha = 0;

        IntPtr stream = FromThumbnail(self)->Stream;
        if (stream == IntPtr.Zero)
            return Com.EUnexpected;

        try
        {
            byte[]? art;
            using (var sng = new ComStream(stream))
                art = AlbumArt.ReadFromSng(sng);

            IntPtr result = art is null ? IntPtr.Zero : ThumbnailBitmap.Create(art, size);
            if (result == IntPtr.Zero)
                return Com.EFail;

            *bitmap = result;
            *alpha = WtsatArgb;
            return Com.SOk;
        }
        catch
        {
            // A malformed .sng or image, or a stream that fails: Explorer shows the type's icon instead.
            return Com.EFail;
        }
    }
}
