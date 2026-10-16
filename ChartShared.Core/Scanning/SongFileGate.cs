namespace ChartShared.Core.Scanning;

/// <summary>
/// Keeps writes to a song apart from reads of it: saving a song waits for copies of it still being
/// made, and copying a song waits while it's being saved. Reads of a song run side by side.
/// </summary>
/// <remarks>
/// Songs are named by their full path, compared exactly. Turns are taken in the order asked for, so
/// a save that's waiting isn't held back by copies started after it. Safe to use from any thread.
/// </remarks>
public sealed class SongFileGate
{
    private readonly object _lock = new();
    private readonly List<Holder> _holders = [];

    /// <summary>Waits until <paramref name="songPaths"/> aren't being written, then holds them for reading until the result is disposed.</summary>
    public Task<IDisposable> ReadAsync(IEnumerable<string> songPaths) => EnterAsync(songPaths, write: false);

    /// <summary>Waits until <paramref name="songPaths"/> aren't being read or written, then holds them for writing until the result is disposed.</summary>
    public Task<IDisposable> WriteAsync(IEnumerable<string> songPaths) => EnterAsync(songPaths, write: true);

    private async Task<IDisposable> EnterAsync(IEnumerable<string> songPaths, bool write)
    {
        ArgumentNullException.ThrowIfNull(songPaths);

        var holder = new Holder(this, songPaths, write);
        if (holder.Songs.Count == 0)
            return holder;

        List<Task> earlier;
        lock (_lock)
        {
            earlier = [.. _holders.Where(h => (write || h.Write) && h.Songs.Overlaps(holder.Songs)).Select(h => h.Released)];
            _holders.Add(holder);
        }

        await Task.WhenAll(earlier).ConfigureAwait(false);
        return holder;
    }

    private void Leave(Holder holder)
    {
        lock (_lock)
            _holders.Remove(holder);
    }

    private sealed class Holder(SongFileGate gate, IEnumerable<string> songs, bool write) : IDisposable
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HashSet<string> Songs { get; } = new(songs, StringComparer.Ordinal);

        public bool Write => write;

        public Task Released => _released.Task;

        public void Dispose()
        {
            if (_released.Task.IsCompleted)
                return;

            gate.Leave(this);
            _released.TrySetResult();
        }
    }
}
