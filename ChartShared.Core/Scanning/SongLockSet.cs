namespace ChartShared.Core.Scanning;

/// <summary>
/// The <see cref="SongLock"/>s one process holds, so that its unsaved changes and its writes can hold the same song at
/// once, and so that it can tell its own locks from another process's (charter-spec §4.8).
/// </summary>
/// <remarks>
/// Each song's lock is taken by its first hold and let go of by its last. A song is known by <see cref="SongLock.KeyOf"/>,
/// so every way of writing its path is the same song. Safe to use from any thread.
/// </remarks>
/// <param name="holder">Which app this is, written into each lock taken.</param>
public sealed class SongLockSet(SongLockHolder holder)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Held> _held = new(StringComparer.Ordinal);

    // The songs whose lock is being taken, each by one thread at a time; a lock taken waits for the file system, and
    // mustn't hold up holds of other songs, or checks, while it does.
    private readonly Dictionary<string, Taking> _taking = new(StringComparer.Ordinal);

    /// <summary>Which app this is.</summary>
    public SongLockHolder Holder { get; } = holder;

    /// <summary>Holds the song at <paramref name="songPath"/>, taking its lock if this is its first hold.</summary>
    /// <returns>The hold, until it's disposed; <see langword="null"/> when another process holds the song.</returns>
    /// <exception cref="IOException">The song's lock can't be taken, though the song can be written to (<see cref="SongLock.TryAcquire"/>).</exception>
    public IDisposable? TryHold(string songPath)
    {
        string key = SongLock.KeyOf(songPath);
        Taking taking;
        lock (_gate)
        {
            if (TryAddHold(key) is { } hold)
                return hold;
            taking = _taking.TryGetValue(key, out Taking? existing) ? existing : _taking[key] = new Taking();
            taking.Count++;
        }

        try
        {
            lock (taking.Gate)
            {
                // Taken by another thread while this one waited.
                lock (_gate)
                {
                    if (TryAddHold(key) is { } hold)
                        return hold;
                }

                if (SongLock.TryAcquire(songPath, Holder) is not { } songLock)
                    return null;

                lock (_gate)
                {
                    _held.Add(key, new Held(songLock));
                    return TryAddHold(key);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--taking.Count == 0)
                    _taking.Remove(key);
            }
        }
    }

    // One more hold of the song with key, when its lock is held; called under _gate.
    private Hold? TryAddHold(string key)
    {
        if (!_held.TryGetValue(key, out Held? held))
            return null;
        held.Count++;
        return new Hold(this, key);
    }

    /// <summary>
    /// Who holds each of the songs at <paramref name="songPaths"/>, when it's another process, as
    /// <see cref="HolderElsewhere"/> says one at a time; but each folder is listed once for the lock files in it, so only
    /// the songs that have one are checked.
    /// </summary>
    /// <remarks>Never throws, as <see cref="SongLock.HolderOf"/>.</remarks>
    public IReadOnlyList<SongLockHolder?> HoldersElsewhere(IReadOnlyList<string> songPaths)
    {
        var holders = new SongLockHolder?[songPaths.Count];
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var lockFilesIn = new Dictionary<string, HashSet<string>?>(comparer);
        for (int i = 0; i < songPaths.Count; i++)
        {
            string lockPath;
            try
            {
                lockPath = SongLock.LockPathOf(songPaths[i]);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            string folder = Path.GetDirectoryName(lockPath) ?? "";
            if (!lockFilesIn.TryGetValue(folder, out HashSet<string>? lockFiles))
                lockFilesIn[folder] = lockFiles = LockFilesIn(folder, comparer);

            // A folder that can't be listed has each of its songs checked.
            if (lockFiles is null || lockFiles.Contains(Path.GetFileName(lockPath)))
                holders[i] = HolderElsewhere(songPaths[i]);
        }

        return holders;
    }

    // The names of the lock files in folder; none when it isn't there, and null when it can't be listed.
    private static HashSet<string>? LockFilesIn(string folder, StringComparer comparer)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, SongLock.SearchPattern).Select(Path.GetFileName).OfType<string>().ToHashSet(comparer)
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether this process holds the song at <paramref name="songPath"/>.</summary>
    public bool Holds(string songPath)
    {
        string key = SongLock.KeyOf(songPath);
        lock (_gate)
            return _held.ContainsKey(key);
    }

    /// <summary>Who holds the song at <paramref name="songPath"/>, when it's another process.</summary>
    /// <returns>The holder; <see langword="null"/> when no one does, or this process does.</returns>
    /// <remarks>Never throws, as <see cref="SongLock.HolderOf"/>.</remarks>
    public SongLockHolder? HolderElsewhere(string songPath)
    {
        try
        {
            if (Holds(songPath))
                return null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return SongLock.HolderOf(songPath);
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(key, out Held? held) || --held.Count > 0)
                return;
            _held.Remove(key);
            held.Lock.Dispose();
        }
    }

    // A song's lock and how many holds it has.
    private sealed class Held(SongLock songLock)
    {
        public SongLock Lock { get; } = songLock;

        public int Count { get; set; }
    }

    // A song whose lock is being taken: the gate its takers wait on, and how many are.
    private sealed class Taking
    {
        public Lock Gate { get; } = new();

        public int Count { get; set; }
    }

    // One hold on a song, let go of once.
    private sealed class Hold(SongLockSet set, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                set.Release(key);
        }
    }
}
