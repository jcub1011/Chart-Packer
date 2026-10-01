using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace ChartPacker.Core.Trash;

/// <summary>The Windows Recycle Bin, through the shell's <c>IFileOperation</c>.</summary>
/// <remarks>
/// <para>
/// Items are deleted with <c>FOFX_RECYCLEONDELETE</c> and <c>FOF_ALLOWUNDO</c>, so they go to the
/// Recycle Bin. When one can't be recycled (a drive with no Recycle Bin, or an item too large
/// for it), <c>FOF_WANTNUKEWARNING</c> makes Windows ask before deleting it permanently, and the
/// answer is reported as <see cref="TrashStatus.DeletedPermanently"/> or <see cref="TrashStatus.Declined"/>.
/// </para>
/// <para>
/// Each item is handled on its own single-threaded apartment thread, as the shell expects.
/// </para>
/// </remarks>
/// <param name="ownerWindow">The native handle of the window a warning belongs to; zero for none. Called off the UI thread.</param>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsRecycleBin(Func<nint> ownerWindow) : ITrash
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x0008_0000;
    private const uint FofxEarlyFailure = 0x0010_0000;
    private const uint Flags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofWantNukeWarning | FofxRecycleOnDelete | FofxEarlyFailure;

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint CoinitDisableOle1Dde = 0x4;
    private const uint ClsctxAll = 0x17;

    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const int EAbort = unchecked((int)0x80004004);

    private static readonly Guid FileOperationClsid = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid FileOperationIid = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public TrashKind Kind => TrashKind.RecycleBin;

    public TrashResult MoveToTrash(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string item = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (isDirectory ? !Directory.Exists(item) : !File.Exists(item))
            return TrashResult.Fail("It no longer exists.");

        nint owner = ownerWindow();
        TrashResult result = TrashResult.Fail("The Recycle Bin couldn't be reached.");
        var thread = new Thread(() => result = Recycle(item, owner)) { IsBackground = true, Name = "Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static TrashResult Recycle(string item, nint owner)
    {
        int init = CoInitializeEx(0, CoinitApartmentThreaded | CoinitDisableOle1Dde);
        nint shellItem = 0;
        IFileOperation? operation = null;
        try
        {
            int hr = CoCreateInstance(FileOperationClsid, 0, ClsctxAll, FileOperationIid, out nint pointer);
            if (hr < 0)
                return Fail(hr);
            try
            {
                operation = (IFileOperation)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                Marshal.Release(pointer);
            }

            hr = SHCreateItemFromParsingName(item, 0, ShellItemIid, out shellItem);
            if (hr < 0)
                return Fail(hr);

            var sink = new DeleteSink();
            operation.SetOperationFlags(Flags);
            if (owner != 0)
                operation.SetOwnerWindow(owner);
            operation.DeleteItem(shellItem, sink);

            hr = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            return Interpret(hr, aborted, sink.Result, sink.Recycled);
        }
        catch (COMException ex)
        {
            return IsCancel(ex.HResult) ? new TrashResult(TrashStatus.Declined) : TrashResult.Fail(ex.Message);
        }
        finally
        {
            if (shellItem != 0)
                Marshal.Release(shellItem);
            if ((object?)operation is ComObject com)
                com.FinalRelease();
            if (init >= 0)
                CoUninitialize();
        }
    }

    /// <summary>What a delete came to, from what the shell reported.</summary>
    /// <param name="hr">What <c>PerformOperations</c> returned.</param>
    /// <param name="aborted">What <c>GetAnyOperationsAborted</c> reported.</param>
    /// <param name="deleteResult">The <c>HRESULT</c> passed to <c>PostDeleteItem</c>; <see langword="null"/> when it wasn't called.</param>
    /// <param name="recycled">Whether <c>PostDeleteItem</c> was given a Recycle Bin item.</param>
    internal static TrashResult Interpret(int hr, bool aborted, int? deleteResult, bool recycled)
    {
        if (IsCancel(hr) || IsCancel(deleteResult))
            return new TrashResult(TrashStatus.Declined);
        if (deleteResult is { } deleted && deleted < 0)
            return Fail(deleted);

        // Errors come before aborts: FOFX_EARLYFAILURE aborts on any failure, before the item is reached.
        if (hr < 0)
            return Fail(hr);

        // Aborted without an error, before the item was deleted: the user said no to deleting it permanently.
        if (aborted && deleteResult is null)
            return new TrashResult(TrashStatus.Declined);
        return deleteResult is not null && !recycled ? new TrashResult(TrashStatus.DeletedPermanently) : TrashResult.Trashed;
    }

    private static bool IsCancel(int? hr) => hr is CopyEngineUserCancelled or ErrorCancelled or EAbort;

    private static TrashResult Fail(int hr) =>
        TrashResult.Fail(Marshal.GetExceptionForHR(hr)?.Message ?? $"Windows reported error 0x{hr:X8}.");

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    /// <summary>Records how the shell deleted the item: whether it created a Recycle Bin item for it.</summary>
    [GeneratedComClass]
    private sealed partial class DeleteSink : IFileOperationProgressSink
    {
        /// <summary>The <c>HRESULT</c> of the delete, once it's been attempted.</summary>
        public int? Result { get; private set; }

        /// <summary>Whether the item went to the Recycle Bin rather than being destroyed.</summary>
        public bool Recycled { get; private set; }

        public void StartOperations() { }
        public void FinishOperations(int result) { }
        public void PreRenameItem(uint flags, nint item, nint newName) { }
        public void PostRenameItem(uint flags, nint item, nint newName, int result, nint created) { }
        public void PreMoveItem(uint flags, nint item, nint destination, nint newName) { }
        public void PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint created) { }
        public void PreCopyItem(uint flags, nint item, nint destination, nint newName) { }
        public void PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint created) { }
        public void PreDeleteItem(uint flags, nint item) { }

        public void PostDeleteItem(uint flags, nint item, int result, nint newlyCreated)
        {
            Result = result;
            Recycled = newlyCreated != 0;
        }

        public void PreNewItem(uint flags, nint destination, nint newName) { }
        public void PostNewItem(uint flags, nint destination, nint newName, nint templateName, uint attributes, int result, nint item) { }
        public void UpdateProgress(uint workTotal, uint workSoFar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }
}

/// <summary>The shell's <c>IFileOperation</c>, in vtable order. Only the methods used have real signatures.</summary>
[GeneratedComInterface]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
internal partial interface IFileOperation
{
    void Advise(nint sink, out uint cookie);
    void Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage(nint message);
    void SetProgressDialog(nint dialog);
    void SetProperties(nint properties);
    void SetOwnerWindow(nint owner);
    void ApplyPropertiesToItem(nint item);
    void ApplyPropertiesToItems(nint items);
    void RenameItem(nint item, nint newName, nint sink);
    void RenameItems(nint items, nint newName);
    void MoveItem(nint item, nint destination, nint newName, nint sink);
    void MoveItems(nint items, nint destination);
    void CopyItem(nint item, nint destination, nint copyName, nint sink);
    void CopyItems(nint items, nint destination);
    void DeleteItem(nint item, IFileOperationProgressSink? sink);
    void DeleteItems(nint items);
    void NewItem(nint destination, uint attributes, nint name, nint templateName, nint sink);

    [PreserveSig]
    int PerformOperations();

    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

/// <summary>The shell's <c>IFileOperationProgressSink</c>, in vtable order. Strings and items are raw pointers, since none are read.</summary>
[GeneratedComInterface]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
internal partial interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int result);
    void PreRenameItem(uint flags, nint item, nint newName);
    void PostRenameItem(uint flags, nint item, nint newName, int result, nint created);
    void PreMoveItem(uint flags, nint item, nint destination, nint newName);
    void PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint created);
    void PreCopyItem(uint flags, nint item, nint destination, nint newName);
    void PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint created);
    void PreDeleteItem(uint flags, nint item);
    void PostDeleteItem(uint flags, nint item, int result, nint newlyCreated);
    void PreNewItem(uint flags, nint destination, nint newName);
    void PostNewItem(uint flags, nint destination, nint newName, nint templateName, uint attributes, int result, nint item);
    void UpdateProgress(uint workTotal, uint workSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}
