using System.Security.Cryptography;
using System.Text;

namespace ChartPacker.Core.Scanning;

/// <summary>
/// Says a song is open in Chart Builder, so that a second Chart Builder won't open it and Chart Packer warns before it
/// writes to it (charter-spec §4.8). Held as a file in the lock folder, named for the song's full path and opened with
/// <see cref="FileShare.None"/>: .NET takes that as <c>flock</c> on Linux and macOS, so it locks on every platform, and
/// the OS lets go of it if the process dies.
/// </summary>
/// <remarks>
/// The lock file is left behind when the lock is let go: deleting it could pull it out from under a process that has
/// just opened it. An empty file in the lock folder means nothing.
/// </remarks>
public sealed class SongLock : IDisposable
{
    private readonly FileStream _file;

    private SongLock(string songPath, FileStream file)
    {
        SongPath = songPath;
        _file = file;
    }

    /// <summary>The song held, as given.</summary>
    public string SongPath { get; }

    /// <summary>
    /// The name of <paramref name="songPath"/>'s lock file: a hash of its full path, ignoring case where the file
    /// system does and any trailing separator.
    /// </summary>
    public static string FileNameOf(string songPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songPath);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(songPath));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            full = full.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
    }

    /// <summary>Takes the lock on <paramref name="songPath"/>, in <paramref name="lockFolder"/>.</summary>
    /// <returns>The lock, held until it's disposed; <see langword="null"/> when another holds it.</returns>
    /// <exception cref="IOException">The lock folder can't be made or written to.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the lock folder is denied.</exception>
    public static SongLock? TryAcquire(string songPath, string lockFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFolder);
        string path = Path.Combine(lockFolder, FileNameOf(songPath));
        Directory.CreateDirectory(lockFolder);

        // Tried twice: IsHeld holds the file for a moment as it checks, which mustn't be taken for another holding it.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new SongLock(songPath, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another holds it: the file is there, but can't be opened.
                if (attempt > 0)
                    return null;
                Thread.Sleep(RetryDelay);
            }
        }
    }

    // How long TryAcquire waits before trying again.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Whether another holds the lock on <paramref name="songPath"/>, in <paramref name="lockFolder"/>.</summary>
    /// <remarks>Never throws: when it can't tell, the song is taken as not held, so the check never stands in the way of a write.</remarks>
    public static bool IsHeld(string songPath, string lockFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFolder);
        string path = Path.Combine(lockFolder, FileNameOf(songPath));
        if (!File.Exists(path))
            return false;

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Lets go of the lock.</summary>
    public void Dispose() => _file.Dispose();
}
