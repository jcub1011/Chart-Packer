using System.Security.Cryptography;
using System.Text;

namespace ChartShared.Core.Scanning;

/// <summary>Which app holds a <see cref="SongLock"/>.</summary>
public enum SongLockHolder
{
    /// <summary>Held, but by an app that can't be told: its lock file can't be read where it's held.</summary>
    Unknown,

    /// <summary>Chart Builder, which holds the song it has open.</summary>
    ChartBuilder,

    /// <summary>Chart Packer, which holds a song while it has unsaved changes to it, and while it writes to it.</summary>
    ChartPacker,
}

/// <summary>
/// Says a song is being edited, so that no other Chart Builder or Chart Packer writes to it (charter-spec §4.8). Held as
/// an open handle on <c>{song}.lock</c> beside the song, which the OS keeps from any other holder.
/// </summary>
/// <remarks>
/// <para>The lock is the open handle, never the file: a lock file nobody has open means nothing, and is taken by the
/// next lock. On Windows the file is opened delete-on-close, which the OS honours however the process ends, and it
/// reads the holder's name while it's held; one left behind that this user may not delete (made from another computer,
/// say) is held without it, and deleted as it's let go if it can be. On Linux and macOS the handle is an exclusive
/// <c>flock</c>, which the OS lets go of if the process dies; the file is deleted as the lock is let go, but is left
/// behind by a crash, and the holder can't be read.</para>
/// <para>Beside the song, the lock also keeps out other users and other computers sharing the library.</para>
/// </remarks>
public sealed class SongLock : IDisposable
{
    // The extension of a lock file, after the song's own name.
    private const string Extension = ".lock";

    // How many times TryAcquire tries, and how long it waits between: a check by HolderOf, or a lock being let go
    // while one is checked, holds the file for a moment, which mustn't be taken for another holding it.
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    // How long ReadHolder waits for a holder that's just made its file to write its name in it.
    private static readonly TimeSpan NameDelay = TimeSpan.FromMilliseconds(20);

    private readonly FileStream? _file;

    // Whether the OS deletes the file as it's closed; on Windows, all but one left behind that this user may not delete.
    private readonly bool _deletesOnClose;

    private SongLock(string songPath, FileStream? file, bool deletesOnClose)
    {
        SongPath = songPath;
        _file = file;
        _deletesOnClose = deletesOnClose;
    }

    /// <summary>The song held, as given.</summary>
    public string SongPath { get; }

    /// <summary>
    /// Whether the lock holds the song. It doesn't when the song can't be written to: no one can write the song there,
    /// so there's nothing to keep out.
    /// </summary>
    internal bool Holds => _file is not null;

    /// <summary>
    /// The lock file of the song at <paramref name="songPath"/>: <c>Foo.sng.lock</c> for <c>Foo.sng</c>, and
    /// <c>Foo.lock</c> beside the song folder <c>Foo</c>, so it's never one of the song's files.
    /// </summary>
    public static string LockPathOf(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(songPath)) + Extension;
    }

    /// <summary>The name of every lock file in a folder, for <see cref="Directory.EnumerateFiles(string, string)"/>.</summary>
    internal const string SearchPattern = "*" + Extension;

    /// <summary>
    /// A key for the song at <paramref name="songPath"/>: a hash of its full path, ignoring case where the file system
    /// does and any trailing separator, so every way of writing the path gives the same key.
    /// </summary>
    public static string KeyOf(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(songPath));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            full = full.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
    }

    /// <summary>Takes the lock on <paramref name="songPath"/>, for <paramref name="holder"/>.</summary>
    /// <returns>
    /// The lock, held until it's disposed; one that doesn't <see cref="Holds"/> when the song can't be written to;
    /// <see langword="null"/> when another holds it.
    /// </returns>
    /// <remarks>
    /// A folder the lock file goes in that isn't there yet, as for a song about to be written for the first time, is
    /// made. Waits up to about a tenth of a second while the lock is checked or let go of elsewhere.
    /// </remarks>
    /// <exception cref="IOException">
    /// The lock file can't be made, though the song can be written to: a song folder whose parent folder can't be written
    /// to, say.
    /// </exception>
    public static SongLock? TryAcquire(string songPath, SongLockHolder holder)
    {
        string path = LockPathOf(songPath);
        Exception? failure = null;
        bool madeFolder = false;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1)
                Thread.Sleep(RetryDelay);

            try
            {
                if (Open(path, holder, out bool deletesOnClose) is { } file)
                    return new SongLock(songPath, file, deletesOnClose);

                // Deleted by the holder before this one as this opened it.
                failure = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another holds it, or is checking it or letting go of it; or the folder is missing or can't be written
                // to. Which is told once the tries run out, as a lock let go of between two checks looks like neither.
                failure = e;
            }

            if (failure is DirectoryNotFoundException && !madeFolder)
            {
                madeFolder = true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failure = e;
                    break;
                }
            }
        }

        if (failure is null || HolderOf(songPath) is not null)
            return null;

        // No one else holds it, so the lock file can't be made here: nothing to keep out if the song can't be written either.
        if (!CanWrite(songPath))
            return new SongLock(songPath, file: null, deletesOnClose: false);
        throw new IOException($"'{songPath}' can't be locked for editing: {failure.Message}", failure);
    }

    // Whether a file can be made where the song at songPath is written: inside a song folder, or beside a .sng.
    private static bool CanWrite(string songPath)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(songPath));
        string? folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
            return false;

        try
        {
            string probe = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose))
                return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Opens the lock file at path for holder; null when it was deleted as it was taken, to be tried again.
    private static FileStream? Open(string path, SongLockHolder holder, out bool deletesOnClose)
    {
        FileStream file;
        deletesOnClose = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            // Sharing reads only, so another holder (which writes) is refused but HolderOf can read the holder.
            try
            {
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                // Left behind where this user may not delete it, so held without that. One being let go refuses this too.
                file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
                deletesOnClose = false;
            }

            try
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only for tidiness.
            }
        }
        else
        {
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            if (!File.Exists(path))
            {
                // The holder before deleted it as this opened it: this holds a file no one else can find.
                file.Dispose();
                return null;
            }
        }

        try
        {
            file.SetLength(0);
            file.Write(Encoding.UTF8.GetBytes(holder.ToString()));
            file.Flush();
        }
        catch (IOException)
        {
            // The holder's name is only for saying who holds it.
        }

        return file;
    }

    // Whether e, from opening the lock file at path, means another has it open: on Windows, a sharing or lock
    // violation; elsewhere, the file is there but its flock was refused.
    private static bool IsSharingViolation(IOException e, string path)
    {
        if (OperatingSystem.IsWindows())
            return (e.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
        return e is not (FileNotFoundException or DirectoryNotFoundException) && File.Exists(path);
    }

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>Who holds the lock on <paramref name="songPath"/>, this process included (<see cref="SongLockSet"/> tells them apart).</summary>
    /// <returns>The holder; <see langword="null"/> when no one holds it.</returns>
    /// <remarks>Never throws: when it can't tell, the song is taken as not held, so the check never stands in the way of a write.</remarks>
    public static SongLockHolder? HolderOf(string songPath)
    {
        string path;
        try
        {
            path = LockPathOf(songPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(path))
            return null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Writing is refused only while a holder has it open. Sharing delete, as a delete-on-close file needs.
                using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1))
                    return null;
            }

            // Reading takes a shared flock, refused while a holder has its exclusive one.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
                return null;
        }
        catch (IOException e) when (IsSharingViolation(e, path))
        {
            return OperatingSystem.IsWindows() ? ReadHolder(path) : SongLockHolder.Unknown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Deleted as it was checked, or being let go.
            return null;
        }
    }

    // The holder named in the held lock file at path. A holder that's only just made it may not have written its name
    // yet, so an empty file is read again a moment later.
    private static SongLockHolder ReadHolder(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            string name;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
                using var reader = new StreamReader(file, Encoding.UTF8);
                name = reader.ReadToEnd().Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return SongLockHolder.Unknown;
            }

            if (name.Length > 0 || attempt == Attempts)
                return Enum.TryParse(name, out SongLockHolder holder) && Enum.IsDefined(holder) ? holder : SongLockHolder.Unknown;
            Thread.Sleep(NameDelay);
        }
    }

    /// <summary>Lets go of the lock, deleting its file.</summary>
    public void Dispose()
    {
        if (_file is null)
            return;

        // Deleted while it's still held, so no one opens it to find it already let go; Windows deletes it on close,
        // or, where it can't, once it's closed, which fails harmlessly if another has taken it meanwhile.
        string path = _file.Name;
        if (!OperatingSystem.IsWindows())
            TryDelete(path);
        _file.Dispose();
        if (OperatingSystem.IsWindows() && !_deletesOnClose)
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind, it means nothing.
        }
    }
}
