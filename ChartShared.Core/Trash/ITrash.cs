namespace ChartShared.Core.Trash;

/// <summary>What the OS calls the place deleted files go.</summary>
public enum TrashKind
{
    /// <summary>The Windows Recycle Bin.</summary>
    RecycleBin,

    /// <summary>The macOS or freedesktop Trash.</summary>
    Trash,
}

/// <summary>What happened to an item sent to the trash.</summary>
public enum TrashStatus
{
    /// <summary>It's in the trash and can be restored.</summary>
    Trashed,

    /// <summary>The OS couldn't recycle it, warned that it would be deleted permanently, and the user agreed.</summary>
    DeletedPermanently,

    /// <summary>The OS warned that it would be deleted permanently and the user declined, so it's untouched.</summary>
    Declined,

    /// <summary>It couldn't be moved to the trash and is untouched.</summary>
    Failed,
}

/// <summary>The outcome of sending one item to the trash.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Error">Why it failed, for <see cref="TrashStatus.Failed"/>.</param>
public sealed record TrashResult(TrashStatus Status, string? Error = null)
{
    public static TrashResult Trashed { get; } = new(TrashStatus.Trashed);

    public static TrashResult Fail(string error) => new(TrashStatus.Failed, error);
}

/// <summary>Moves files and folders to the OS trash, where they can be restored.</summary>
/// <remarks>
/// An implementation never deletes anything itself. When the OS can't keep an item, it either
/// fails or, on Windows, leaves the choice to the OS's own "delete permanently?" warning.
/// </remarks>
public interface ITrash
{
    /// <summary>What the OS calls the trash.</summary>
    TrashKind Kind { get; }

    /// <summary>Moves the file or folder at <paramref name="path"/> to the trash.</summary>
    TrashResult MoveToTrash(string path, bool isDirectory);
}

/// <summary>The trash of the OS this runs on.</summary>
public static class SystemTrash
{
    /// <summary>
    /// The OS trash, or <see langword="null"/> when this OS has none that's supported.
    /// </summary>
    /// <param name="ownerWindow">
    /// On Windows, the native handle of the window any OS warning belongs to; <see cref="nint.Zero"/> for none.
    /// </param>
    public static ITrash? ForCurrentOS(Func<nint>? ownerWindow = null)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsRecycleBin(ownerWindow ?? (() => 0));
        if (OperatingSystem.IsMacOS())
            return MacTrash.IsAvailable ? new MacTrash() : null;
        if (OperatingSystem.IsLinux())
            return FreedesktopTrash.ForCurrentUser();
        return null;
    }
}
