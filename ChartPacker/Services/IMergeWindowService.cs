using System.Collections.Generic;
using System.Threading.Tasks;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Services
{
    public interface IMergeWindowService
    {
        /// <summary>
        /// Shows the Merge window for <paramref name="entries"/> and completes when it closes.
        /// </summary>
        /// <param name="entries">The songs to merge, in the order their columns are shown.</param>
        /// <param name="outputFolder">Where the merged <c>.sng</c> goes.</param>
        /// <param name="policy">What to do when it already exists.</param>
        /// <returns>The merged <c>.sng</c>, or <see langword="null"/> when nothing was merged.</returns>
        Task<string?> ShowAsync(IReadOnlyList<SongEntry> entries, string outputFolder, OverwritePolicy policy);
    }
}
