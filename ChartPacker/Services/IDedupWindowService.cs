using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>How far the library scan the Deduplicate window waits on has got.</summary>
    /// <param name="Read">How many songs have been read and listed.</param>
    /// <param name="Found">How many songs have been found so far.</param>
    /// <param name="IsSearching">Folders are still being searched, so <paramref name="Found"/> may grow.</param>
    public sealed record DedupScanProgress(int Read, int Found, bool IsSearching);

    /// <summary>What the Deduplicate window compares, and where a merge it starts writes.</summary>
    /// <param name="LoadEntries">
    /// Waits for the library scan, reporting its progress, and gives every song in the library; or
    /// <see langword="null"/> when the scan stopped short. Called on the UI thread.
    /// </param>
    /// <param name="OutputFolder">Where a merged <c>.sng</c> goes.</param>
    /// <param name="Policy">What a merge does when its output already exists.</param>
    /// <param name="Parallelism">How many songs are read, and files hashed, at once.</param>
    public sealed record DedupRequest(
        Func<IProgress<DedupScanProgress>, CancellationToken, Task<IReadOnlyList<SongEntry>?>> LoadEntries,
        string OutputFolder,
        OverwritePolicy Policy,
        int Parallelism);

    /// <summary>What the Deduplicate window changed on disk.</summary>
    /// <param name="RemovedPaths">Songs moved to the trash or deleted.</param>
    /// <param name="OutputsChanged">A merge wrote a new song, so the library needs a rescan to show it.</param>
    public sealed record DedupWindowResult(IReadOnlyList<string> RemovedPaths, bool OutputsChanged)
    {
        public static DedupWindowResult None { get; } = new([], false);
    }

    public interface IDedupWindowService
    {
        /// <summary>Shows the Deduplicate window for <paramref name="request"/> and completes when it closes.</summary>
        Task<DedupWindowResult> ShowAsync(DedupRequest request);
    }
}
