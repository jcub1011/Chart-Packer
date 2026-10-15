using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChartShared.Core.Trash;

/// <summary>The macOS Trash, through <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>.</summary>
/// <remarks>
/// Foundation never deletes an item it can't trash; it fails with an <c>NSError</c>, whose
/// description is passed on.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacTrash : ITrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            return NativeLibrary.TryLoad(Foundation, out _) && objc_getClass("NSFileManager") != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    });

    /// <summary>Whether Foundation can be reached.</summary>
    public static bool IsAvailable => Available.Value;

    public TrashKind Kind => TrashKind.Trash;

    public TrashResult MoveToTrash(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string item = Path.GetFullPath(path);
        if (isDirectory ? !Directory.Exists(item) : !File.Exists(item))
            return TrashResult.Fail("It no longer exists.");

        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            nint text = SendUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), item);
            nint url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), text);
            if (manager == 0 || url == 0)
                return TrashResult.Fail("The Trash couldn't be reached.");

            byte ok = SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, 0, out nint error);
            if (ok != 0)
                return TrashResult.Trashed;

            return TrashResult.Fail(Describe(error) ?? "The item couldn't be moved to the Trash.");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static string? Describe(nint error)
    {
        if (error == 0)
            return null;
        nint description = Send(error, sel_registerName("localizedDescription"));
        return description == 0 ? null : Marshal.PtrToStringUTF8(Send(description, sel_registerName("UTF8String")));
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendUtf8(nint receiver, nint selector, string argument);

    // BOOL is a one-byte type on every macOS architecture.
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);
}
