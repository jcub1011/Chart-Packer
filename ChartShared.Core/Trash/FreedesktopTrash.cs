using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ChartShared.Core.Trash;

/// <summary>The Trash of Linux desktops, following the freedesktop.org Trash specification 1.0.</summary>
/// <remarks>
/// <para>
/// Written from the specification (<see href="https://specifications.freedesktop.org/trash-spec/latest/"/>).
/// An item on the same mount as the home trash goes there. An item on another mount goes to
/// that mount's <c>$topdir/.Trash/$uid</c> when an administrator has set up <c>$topdir/.Trash</c>
/// (a real directory with the sticky bit), or else to <c>$topdir/.Trash-$uid</c>.
/// </para>
/// <para>
/// An item is only ever renamed into a trash on its own mount, never copied across, and an item
/// whose mount has no usable trash fails rather than being deleted.
/// </para>
/// </remarks>
public sealed partial class FreedesktopTrash : ITrash
{
    private const string FilesFolder = "files";
    private const string InfoFolder = "info";
    private const string InfoExtension = ".trashinfo";

    private readonly string _homeTrash;
    private readonly Func<string, string?> _mountPointOf;
    private readonly string _uid;
    private readonly Func<DateTime> _now;
    private readonly Func<string, string> _resolveLinks;

    /// <param name="homeTrash">The home trash, normally <c>$XDG_DATA_HOME/Trash</c>.</param>
    /// <param name="mountPointOf">The mount point a path is on.</param>
    /// <param name="uid">The user's numeric id.</param>
    /// <param name="now">The local time, for <c>DeletionDate</c>.</param>
    /// <param name="resolveLinks">
    /// The path a path really is, with every symbolic link followed, so a path reached through a
    /// link onto another mount is looked up on that mount. Paths are used as given when this is
    /// <see langword="null"/>.
    /// </param>
    public FreedesktopTrash(string homeTrash, Func<string, string?> mountPointOf, string uid, Func<DateTime>? now = null, Func<string, string>? resolveLinks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeTrash);
        ArgumentNullException.ThrowIfNull(mountPointOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        _homeTrash = Path.GetFullPath(homeTrash);
        _mountPointOf = mountPointOf;
        _uid = uid;
        _now = now ?? (() => DateTime.Now);
        _resolveLinks = resolveLinks ?? (path => path);
    }

    public TrashKind Kind => TrashKind.Trash;

    /// <summary>The current user's trash, or <see langword="null"/> when the mounts or user can't be found.</summary>
    public static FreedesktopTrash? ForCurrentUser()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        if (MountTable.LoadCurrent() is not { } mounts || CurrentUid() is not { } uid)
            return null;

        return new FreedesktopTrash(Path.Combine(dataHome, "Trash"), mounts.MountPointOf, uid, resolveLinks: ResolveLinks);
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link followed. The part that doesn't exist yet,
    /// such as a trash not yet created, is kept as it is under its nearest existing folder.
    /// </summary>
    internal static string ResolveLinks(string path)
    {
        string full = Path.GetFullPath(path);
        string? existing = full;
        while (existing is not null && !Path.Exists(existing))
            existing = Path.GetDirectoryName(existing);
        if (existing is null || RealPath(existing) is not { } real)
            return full;

        return existing.Length == full.Length ? real : Path.Join(real, full[existing.Length..].TrimStart('/'));
    }

    private static string? RealPath(string path)
    {
        nint resolved = RealPathNative(path, 0);
        if (resolved == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            unsafe
            {
                NativeMemory.Free((void*)resolved);
            }
        }
    }

    /// <summary>POSIX <c>realpath</c>; with no buffer given, it allocates the result, which is freed with <c>free</c>.</summary>
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RealPathNative(string path, nint resolved);

    public TrashResult MoveToTrash(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string item = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (isDirectory ? !Directory.Exists(item) : !File.Exists(item))
            return TrashResult.Fail("It no longer exists.");

        try
        {
            // Mounts are looked up where the item really is. Only its folder is followed, since
            // renaming a link moves the link, not what it points to.
            string real = Path.GetDirectoryName(item) is { } parent ? Path.Combine(_resolveLinks(parent), Path.GetFileName(item)) : item;
            if (FindTrash(real) is not { } found)
                return TrashResult.Fail("This drive has no Trash.");

            var (trash, topdir) = found;
            Directory.CreateDirectory(Path.Combine(trash, FilesFolder));
            Directory.CreateDirectory(Path.Combine(trash, InfoFolder));

            string infoPath = CreateInfo(trash, Path.GetFileName(item), topdir is null ? real : Path.GetRelativePath(topdir, real).Replace(Path.DirectorySeparatorChar, '/'), out string name);
            try
            {
                // Checked again just before moving, so the item is renamed, never copied.
                if (_mountPointOf(real) != _mountPointOf(_resolveLinks(trash)))
                    throw new IOException("The Trash is on a different drive.");

                string target = Path.Combine(trash, FilesFolder, name);
                if (isDirectory)
                    Directory.Move(item, target);
                else
                    File.Move(item, target);
            }
            catch
            {
                File.Delete(infoPath);
                throw;
            }

            return TrashResult.Trashed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Fail(ex.Message);
        }
    }

    /// <summary>The trash for <paramref name="item"/>, and the top directory its info paths are relative to (none for the home trash).</summary>
    private (string Trash, string? Topdir)? FindTrash(string item)
    {
        string? mount = _mountPointOf(item);
        if (mount is null)
            return null;

        if (_mountPointOf(_resolveLinks(_homeTrash)) == mount)
            return (_homeTrash, null);

        string shared = Path.Combine(mount, ".Trash");
        if (IsSharedTrash(shared))
        {
            string mine = Path.Combine(shared, _uid);
            CreatePrivateDirectory(mine);
            if (IsRealDirectory(mine))
                return (mine, mount);
        }

        string own = Path.Combine(mount, ".Trash-" + _uid);
        CreatePrivateDirectory(own);
        return IsRealDirectory(own) ? (own, mount) : null;
    }

    /// <summary>Creates <c>info/&lt;name&gt;.trashinfo</c> under a free name, which is also the name the item gets in <c>files/</c>.</summary>
    private string CreateInfo(string trash, string baseName, string originalPath, out string name)
    {
        string contents = "[Trash Info]\nPath=" + Escape(originalPath) + "\nDeletionDate="
            + _now().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

        for (int n = 1; ; n++)
        {
            name = n == 1 ? baseName : baseName + "." + n.ToString(CultureInfo.InvariantCulture);
            string infoPath = Path.Combine(trash, InfoFolder, name + InfoExtension);
            if (Path.Exists(Path.Combine(trash, FilesFolder, name)))
                continue;

            try
            {
                // CreateNew claims the name atomically, as the specification requires.
                using var info = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                info.Write(Encoding.UTF8.GetBytes(contents));
                return infoPath;
            }
            catch (IOException) when (File.Exists(infoPath))
            {
            }
        }
    }

    /// <summary>Percent-encodes a path as a URL path, leaving <c>/</c> and unreserved characters as they are.</summary>
    internal static string Escape(string path)
    {
        var text = new StringBuilder(path.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()/".Contains(c))
                text.Append(c);
            else
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Whether <paramref name="path"/> is an administrator's shared trash: a real directory with the sticky bit.</summary>
    private static bool IsSharedTrash(string path)
    {
        if (!IsRealDirectory(path) || OperatingSystem.IsWindows())
            return false;
        return File.GetUnixFileMode(path).HasFlag(UnixFileMode.StickyBit);
    }

    private static bool IsRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.LinkTarget is null;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (Path.Exists(path))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string? CurrentUid()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                // "Uid:\t1000\t1000\t1000\t1000": the real uid comes first.
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line[4..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
