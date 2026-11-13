using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>What the Optimize window works on.</summary>
    /// <param name="Selected">The songs selected in the library; none when the window opens on the whole library.</param>
    /// <param name="Library">
    /// Every song in the library, as it was last scanned. The window reads each chart's files itself, so it needs no fresh
    /// scan, and the main window's list and selection stay as they are while it's open.
    /// </param>
    public sealed record OptimizeRequest(IReadOnlyList<SongEntry> Selected, IReadOnlyList<SongEntry> Library);

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
    /// <param name="Estimates">What's been measured of the charts, kept between sessions so they aren't measured again.</param>
    /// <param name="Previews">Makes before-and-after pictures of a chart's images and video; none when null.</param>
    public sealed record OptimizeBackends(
        Func<int, OptimizerSet> CreateOptimizers,
        AssetProbes Probes,
        Func<OptimizeStrategy, string?> UnavailableReason,
        EstimateCache Estimates,
        IMediaPreviewer? Previews = null);

    /// <summary>Opens the Optimize window (issue #257), over the library's songs.</summary>
    public interface IOptimizeWindowService
    {
        /// <summary>Shows the Optimize window for <paramref name="request"/> and completes when it closes.</summary>
        Task<OptimizeWindowResult> ShowAsync(OptimizeRequest request);
    }
}
