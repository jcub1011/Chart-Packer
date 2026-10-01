using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChartPacker.Core.Scanning;

/// <summary>A song the library scan found, with its metadata read.</summary>
/// <param name="Summary">What was read, or <see langword="null"/> when the song couldn't be read.</param>
public sealed record LoadedSong(SongEntry Entry, ScanPosition Position, SongSummary? Summary);

/// <summary>How far a library load has got, with the songs read since the last report.</summary>
/// <param name="Songs">The songs read since the last report, in no particular order.</param>
/// <param name="Found">How many songs have been found so far.</param>
/// <param name="Read">How many songs have been reported so far, these included.</param>
/// <param name="SearchComplete">Every folder has been searched, so <paramref name="Found"/> is final.</param>
public sealed record LibraryLoadProgress(IReadOnlyList<LoadedSong> Songs, int Found, int Read, bool SearchComplete);

/// <summary>
/// Scans a folder for songs and reads each one's metadata as it's found, reporting the songs in
/// batches once they've been read.
/// </summary>
public static class SongLibraryLoader
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Finds the songs under <paramref name="root"/> and reads their metadata, both on the thread
    /// pool. Up to <paramref name="parallelism"/> folders are searched, and up to
    /// <paramref name="parallelism"/> songs read, at once.
    /// </summary>
    /// <remarks>
    /// Reports come about every 100 ms while there's something new, one at a time, and a last one
    /// once everything has been read. Sort the songs by <see cref="LoadedSong.Position"/> for the
    /// scan's order.
    /// </remarks>
    /// <returns>The total number of songs found.</returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="root"/> doesn't exist.</exception>
    /// <exception cref="OperationCanceledException">The load was cancelled.</exception>
    public static Task<int> LoadAsync(
        string root,
        int maxDepth,
        int parallelism,
        IProgress<LibraryLoadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);

        // Either stage failing stops the other.
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new LoadState();
        Task<int> search;
        try
        {
            search = SongLibraryScanner.ScanAsync(root, maxDepth, parallelism, song =>
            {
                Interlocked.Increment(ref state.Found);
                state.Songs.Writer.TryWrite(song);
            }, stop.Token);
        }
        catch
        {
            stop.Dispose();
            throw;
        }

        return RunAsync(search, parallelism, progress, state, stop);
    }

    private static async Task<int> RunAsync(
        Task<int> search,
        int parallelism,
        IProgress<LibraryLoadProgress> progress,
        LoadState state,
        CancellationTokenSource stop)
    {
        using var _ = stop;

        var work = Task.WhenAll(FinishSearchAsync(search, state, stop), ReadAllAsync(parallelism, state, stop));
        using (var timer = new PeriodicTimer(ReportInterval))
        {
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, timer.WaitForNextTickAsync().AsTask()).ConfigureAwait(false);
                if (!work.IsCompleted)
                    Report(progress, state, final: false);
            }
        }

        await work.ConfigureAwait(false);
        Report(progress, state, final: true);
        return await search.ConfigureAwait(false);
    }

    /// <summary>Waits for the search, then lets the readers finish once they've caught up.</summary>
    private static async Task FinishSearchAsync(Task<int> search, LoadState state, CancellationTokenSource stop)
    {
        try
        {
            await search.ConfigureAwait(false);
            state.SearchComplete = true;
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            state.Songs.Writer.TryComplete();
        }
    }

    private static async Task ReadAllAsync(int parallelism, LoadState state, CancellationTokenSource stop)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stop.Token };
        try
        {
            await Parallel.ForEachAsync(state.Songs.Reader.ReadAllAsync(stop.Token), options, async (song, token) =>
            {
                var summary = await SongSummaryReader.TryReadAsync(song.Entry, token).ConfigureAwait(false);
                state.Read.Enqueue(new LoadedSong(song.Entry, song.Position, summary));
            }).ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reports what's been read since the last report, if anything has changed.</summary>
    private static void Report(IProgress<LibraryLoadProgress> progress, LoadState state, bool final)
    {
        // Read before the found count, so a song is never reported before it has been counted as found.
        var songs = new List<LoadedSong>();
        while (state.Read.TryDequeue(out var song))
            songs.Add(song);

        int found = Volatile.Read(ref state.Found);
        bool searchComplete = state.SearchComplete;
        if (!final && songs.Count == 0 && found == state.ReportedFound && searchComplete == state.ReportedSearchComplete)
            return;

        state.Reported += songs.Count;
        state.ReportedFound = found;
        state.ReportedSearchComplete = searchComplete;
        progress.Report(new LibraryLoadProgress(songs, found, state.Reported, searchComplete));
    }

    private sealed class LoadState
    {
        public readonly Channel<ScannedSong> Songs = Channel.CreateUnbounded<ScannedSong>();
        public readonly ConcurrentQueue<LoadedSong> Read = new();
        public int Found;
        public volatile bool SearchComplete;

        // Only touched by the reporting loop.
        public int Reported;
        public int ReportedFound;
        public bool ReportedSearchComplete;
    }
}
