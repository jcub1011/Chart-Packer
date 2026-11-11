using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>What the Optimize window works on.</summary>
    /// <param name="Selected">The songs selected in the library; none when the window opens on the whole library.</param>
    /// <param name="LoadLibrary">
    /// Rescans the library, reporting the scan's progress, and gives every song in it; or <see langword="null"/> when the
    /// scan stopped short. Each call is a fresh scan, so it sees what an earlier run changed. Called on the UI thread.
    /// </param>
    public sealed record OptimizeRequest(
        IReadOnlyList<SongEntry> Selected,
        Func<IProgress<DedupScanProgress>, CancellationToken, Task<IReadOnlyList<SongEntry>?>> LoadLibrary);

    /// <summary>What the Optimize window changed on disk.</summary>
    /// <param name="ChangedInPlace">Charts were rewritten where they are, so the library needs a rescan to show their sizes.</param>
    /// <param name="OutputsChanged">Charts were written to the output folder, so a rescan of that folder would show them.</param>
    public sealed record OptimizeWindowResult(bool ChangedInPlace, bool OutputsChanged)
    {
        public static OptimizeWindowResult None { get; } = new(false, false);
    }

    /// <summary>The optimizers, probes and availability the Optimize window runs with.</summary>
    /// <param name="CreateOptimizers">
    /// Makes the set a run uses, for the number of charts it works on at once, which the video optimizer shares the
    /// machine's cores between.
    /// </param>
    /// <param name="Probes">Reads the files of a chart, so they can be grouped by codec.</param>
    /// <param name="UnavailableReason">
    /// Why a strategy can't run (video without the libvpx encoder, for one), or <see langword="null"/> when it can.
    /// </param>
    public sealed record OptimizeBackends(
        Func<int, OptimizerSet> CreateOptimizers,
        AssetProbes Probes,
        Func<OptimizeStrategy, string?> UnavailableReason);

    /// <summary>Opens the Optimize window (issue #257), over the library's songs.</summary>
    public interface IOptimizeWindowService
    {
        /// <summary>Shows the Optimize window for <paramref name="request"/> and completes when it closes.</summary>
        Task<OptimizeWindowResult> ShowAsync(OptimizeRequest request);
    }
}
