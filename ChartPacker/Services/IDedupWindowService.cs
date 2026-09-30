using System.Collections.Generic;
using System.Threading.Tasks;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>What the Deduplicate window compares, and where a merge it starts writes.</summary>
    /// <param name="Entries">Every song in the library.</param>
    /// <param name="OutputFolder">Where a merged <c>.sng</c> goes.</param>
    /// <param name="Policy">What a merge does when its output already exists.</param>
    public sealed record DedupRequest(IReadOnlyList<SongEntry> Entries, string OutputFolder, OverwritePolicy Policy);

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
