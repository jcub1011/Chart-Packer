using ChartShared.Core.Optimization;

namespace ChartShared.Video;

/// <summary>
/// The Library Optimization Tool's video optimizer: re-encodes a background video to VP8 in WebM with no audio track
/// (<see cref="VideoReencoder"/>), the one format that plays everywhere in both games, fitted inside FHD or UHD at the
/// chosen libvpx CQ.
/// </summary>
/// <remarks>
/// <para><b>Already optimal.</b> A video that's already VP8 in a <c>.webm</c>, within the target size and not lavish with
/// bits (under <see cref="LavishBitsPerPixelFrame"/>), is reported <see cref="OptimizeEstimate.AlreadyOptimal"/> without
/// being encoded, so it's skipped: encoding lossy video again to the same codec costs quality for little saving. A VP8
/// file with far more bits than that is estimated like any other, so the planner's margin decides. It's judged by probing
/// the file itself, not by what the inspector recorded. (Its audio track, if it has one, stays: stripping it isn't worth
/// a re-encode.)</para>
/// <para><b>Estimates.</b> <see cref="VideoReencoder.EstimateSize"/> encodes equally spaced windows of the video, one
/// keyframe interval each, and scales their size per frame to the whole video; a short video is encoded whole. Only the
/// windows are decoded, so damage elsewhere passes the estimate and fails the optimize. It's the size of what would be
/// written; what it's compared with, and so the skip rules (below the margin, larger than the input, and the exemption of
/// a conversion for compatibility), are <see cref="OptimizationPlanner"/>'s.</para>
/// <para><b>Speed and cores.</b> <see cref="VideoOptimizeOptions.Speed"/> sets how hard libvpx works, for the estimate as
/// for the encode. Any number of calls may run at once (the job runs <see cref="OptimizeOptions.Parallelism"/> charts at a
/// time); each encode's threads are the machine's cores divided by the most of <paramref name="expectedConcurrency"/> and the
/// calls running when it started, so that the calls of a job that runs <see cref="OptimizeOptions.Parallelism"/> at once
/// share the machine from the first, rather than the first taking it all.</para>
/// <para><b>Availability.</b> It needs FFmpeg built with the libvpx encoder; <see cref="IsAvailable"/> is false otherwise,
/// and the planner leaves videos alone with <see cref="OptimizeSkipReason.OptimizerUnavailable"/> (the UI can show the
/// option disabled, with <see cref="VideoReencoder.UnavailableReason"/>).</para>
/// </remarks>
/// <param name="windowCount">How many windows an estimate encodes.</param>
/// <param name="windowFrames">How many frames each window is.</param>
/// <param name="expectedConcurrency">How many calls the host runs at once, as <see cref="OptimizeOptions.Parallelism"/>; 1 or less when it runs them one at a time.</param>
public sealed class VideoOptimizer(
    int windowCount = VideoOptimizer.DefaultWindowCount, int windowFrames = VideoReencoder.DefaultWindowFrames, int expectedConcurrency = 1) : IVideoOptimizer
{
    /// <summary>How many windows an estimate encodes by default.</summary>
    public const int DefaultWindowCount = VideoReencoder.DefaultWindowCount;

    /// <summary>
    /// The bits per pixel per frame above which a VP8 file is too lavish to be left alone: 0.1 is 6 Mbps for 1080p30, several
    /// times what libvpx writes there at the default CQ. A file that rich may shrink a good deal, so it's estimated.
    /// </summary>
    public const double LavishBitsPerPixelFrame = 0.1;

    /// <summary>The extension of what it writes.</summary>
    public const string OutputExtension = ".webm";

    private readonly int _windowCount = windowCount >= 1 ? windowCount : throw new ArgumentOutOfRangeException(nameof(windowCount));
    // How many encodes and estimates are running, so each gets its share of the machine's cores rather than all of them.
    private int _active;

    private readonly int _windowFrames = windowFrames >= 1 ? windowFrames : throw new ArgumentOutOfRangeException(nameof(windowFrames));

    /// <inheritdoc />
    public bool IsAvailable => VideoReencoder.IsAvailable;

    /// <inheritdoc />
    public Task<OptimizeEstimate> EstimateAsync(AssetFile source, VideoOptimizeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() =>
        {
            var settings = Begin(options);
            try
            {
                return Estimate(source, settings, cancellationToken);
            }
            finally
            {
                End();
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task OptimizeAsync(AssetFile source, string destinationPath, VideoOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);

        // Reports on the calling thread: Progress<T> would post to a synchronization context, and arrive out of order.
        var inline = progress is null ? null : new InlineProgress(progress.Report);
        return Task.Run(() =>
        {
            var settings = Begin(options);
            try
            {
                VideoReencoder.Reencode(source.Path, destinationPath, settings, inline, cancellationToken);
            }
            finally
            {
                End();
            }
        }, cancellationToken);
    }

    // An encode's threads are the machine's cores (up to the 8 libvpx can use) over the calls expected at once, or running now
    // if that's more: fixed when the encode starts, so the first of several doesn't take the whole machine before the rest
    // have begun.
    private VideoReencodeSettings Begin(VideoOptimizeOptions options)
    {
        int active = Interlocked.Increment(ref _active);
        int sharing = Math.Max(Math.Max(expectedConcurrency, 1), active);
        return VideoReencodeSettings.From(options) with { Threads = Math.Clamp(Environment.ProcessorCount / sharing, 1, 8) };
    }

    private void End() => Interlocked.Decrement(ref _active);

    /// <summary>
    /// Whether a probed video is VP8 in a <c>.webm</c> that fits inside the target and isn't lavish with bits, so encoding it
    /// again gains nothing. A video whose length or frame rate isn't known can't be judged lavish.
    /// </summary>
    internal static bool IsAlreadyOptimal(ChartAsset asset, VideoInfo info, VideoReencodeSettings settings)
    {
        var (maxWidth, maxHeight) = settings.MaxSize;
        return asset.Extension.Equals(OutputExtension, StringComparison.OrdinalIgnoreCase)
            && info.Codec.Equals("vp8", StringComparison.OrdinalIgnoreCase)
            && info.Width <= maxWidth
            && info.Height <= maxHeight
            && !IsLavish(asset.Length, info);
    }

    private static bool IsLavish(long length, VideoInfo info)
    {
        double pixelFrames = (double)info.Width * info.Height * info.FrameRate * info.Duration.TotalSeconds;
        return pixelFrames > 0 && length * 8 / pixelFrames > LavishBitsPerPixelFrame;
    }

    private OptimizeEstimate Estimate(AssetFile source, VideoReencodeSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = VideoProbe.Probe(source.Path);
        if (IsAlreadyOptimal(source.Asset, info, settings))
            return new OptimizeEstimate(source.Asset.Length, OutputExtension, AlreadyOptimal: true);

        return new OptimizeEstimate(VideoReencoder.EstimateSize(source.Path, settings, _windowCount, _windowFrames, cancellationToken: cancellationToken), OutputExtension);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
