namespace ChartShared.Core.Optimization;

/// <summary>One of the ways the Library Optimization Tool can shrink a chart.</summary>
public enum OptimizeStrategy
{
    /// <summary>Leaves out metadata no game needs (<see cref="MetadataCleanupOptions"/>).</summary>
    Metadata,

    /// <summary>Transcodes each audio stem.</summary>
    Audio,

    /// <summary>Mixes a chart's stems down to a single <c>song</c> stem.</summary>
    StemMixdown,

    /// <summary>Recompresses the background video.</summary>
    Video,

    /// <summary>Recompresses the album art.</summary>
    AlbumArt,

    /// <summary>Recompresses the background image.</summary>
    BackgroundArt,
}

/// <summary>The size a video is brought down to.</summary>
public enum VideoTarget
{
    /// <summary>Full HD, 1920x1080.</summary>
    Fhd,

    /// <summary>Ultra HD, 3840x2160.</summary>
    Uhd,
}

/// <summary>How hard the video encoder works. Slower takes longer and gives a smaller file, or a better picture, for the same CQ.</summary>
public enum VideoEncodeSpeed
{
    /// <summary>The quickest, about twice as fast as <see cref="Slow"/>, at 10 to 20% more bits for the same picture.</summary>
    Fast,

    /// <summary>A little slower than <see cref="Fast"/>'s half-way: about 1.5 times as fast as <see cref="Slow"/>, within a few percent of its efficiency. The default.</summary>
    Balanced,

    /// <summary>libvpx's own default speed.</summary>
    Slow,

    /// <summary>The slowest and most efficient; about 5 to 10% fewer bits than <see cref="Slow"/>, in about 20% more time.</summary>
    Best,
}

/// <summary>Which parts of metadata cleanup run. Every step is optional.</summary>
/// <param name="Enabled">Whether metadata cleanup runs at all.</param>
/// <param name="PruneEmptyParts">Drop the <c>song.ini</c> keys of parts the chart has no notes for (SNG062).</param>
/// <param name="StripEditorEvents">Drop the tempo anchors only Chart Builder reads from <c>notes.mid</c>.</param>
/// <param name="DropEmptyValues">Drop keys with an empty value (SNG061).</param>
/// <param name="ReplaceLegacyKeys">Rename legacy keys to their replacements (SNG025).</param>
public sealed record MetadataCleanupOptions(
    bool Enabled = false,
    bool PruneEmptyParts = true,
    bool StripEditorEvents = true,
    bool DropEmptyValues = true,
    bool ReplaceLegacyKeys = true)
{
    /// <summary>Cleanup off, every step on should it be enabled.</summary>
    public static MetadataCleanupOptions Default { get; } = new();
}

/// <summary>Audio transcoding and stem mixdown settings.</summary>
/// <param name="Enabled">Whether the strategy runs.</param>
/// <param name="BitrateKbps">The bitrate of the Opus it writes, per stereo file; a mono file gets half.</param>
public sealed record AudioOptimizeOptions(bool Enabled = false, int BitrateKbps = AudioOptimizeOptions.DefaultBitrateKbps)
{
    /// <summary>The default bitrate, 128 kbps per stereo stem: the Chart Packer export default (<c>OpusQuality.Music</c>).</summary>
    public const int DefaultBitrateKbps = 128;

    /// <summary>The "smaller" preset's bitrate, 80 kbps per stereo stem: SngCli's default.</summary>
    public const int SmallerBitrateKbps = 80;

    /// <summary>Off, at 128 kbps.</summary>
    public static AudioOptimizeOptions Default { get; } = new();

    /// <summary>Off, at the "smaller" preset's 80 kbps.</summary>
    public static AudioOptimizeOptions Smaller { get; } = new(false, SmallerBitrateKbps);
}

/// <summary>Video recompression settings.</summary>
/// <param name="Enabled">Whether the strategy runs.</param>
/// <param name="Target">The size the video is brought down to.</param>
/// <param name="Cq">libvpx's constant-quality value, 4 to 63 (FFmpeg's <c>-crf</c> for VP8); lower is better quality and a larger file.</param>
/// <param name="Remove">Delete the video instead of recompressing it.</param>
/// <param name="ConvertUnsupported">
/// Also convert videos in a codec the games can't all play (<see cref="VideoCodecs.IsUnsupported"/>: VP9, AV1, HEVC) to
/// VP8 WebM, which plays everywhere. Such a conversion is for compatibility, not size: it's done whatever it saves, and
/// the file that results may be larger. Off, those videos are left alone, and only an estimate shows what converting would cost.
/// </param>
/// <param name="Speed">How hard the encoder works; slower is more efficient. An estimate is made at the same speed, so it matches the encode.</param>
/// <remarks>Videos are written as VP8 in WebM, with no audio track, and a video that's already VP8 WebM within the target size is left alone.</remarks>
public sealed record VideoOptimizeOptions(bool Enabled = false, VideoTarget Target = VideoTarget.Fhd, int Cq = VideoOptimizeOptions.DefaultCq, bool Remove = false, bool ConvertUnsupported = false, VideoEncodeSpeed Speed = VideoEncodeSpeed.Balanced)
{
    /// <summary>
    /// The default CQ, 32: what the author's own compressed videos used (HandBrake), and about 40% smaller than 10 on real
    /// 1080p videos. The FFmpeg wiki suggests starting at 10, which is far larger than most sources.
    /// </summary>
    public const int DefaultCq = 32;

    /// <summary>Off, FHD at CQ 32, leaving unsupported codecs alone.</summary>
    public static VideoOptimizeOptions Default { get; } = new();
}

/// <summary>Which video codecs the games can play.</summary>
public static class VideoCodecs
{
    // VP8 in WebM is the only video that plays on every platform in both Clone Hero and YARG. VP9, AV1 and HEVC aren't
    // listed as supported anywhere (HEVC plays in Clone Hero only with codecs installed); H.264 and the older codecs play on
    // some platforms, so they're recompressed under the usual rules rather than converted as a matter of compatibility.
    private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase) { "video/vp9", "video/av1", "video/hevc" };

    /// <summary>Whether a video whose probed codec is <paramref name="codecKey"/> (<c>video/vp9</c>) is in a codec the games can't all play.</summary>
    public static bool IsUnsupported(string? codecKey) => codecKey is not null && Unsupported.Contains(codecKey);
}

/// <summary>Image recompression settings, for album art or the background image.</summary>
/// <param name="Enabled">Whether the strategy runs.</param>
/// <param name="Quality">The JPEG quality, 1 to 100.</param>
/// <param name="MaxWidth">The widest an image is left; a wider one is scaled down to fit. <see langword="null"/> sets no limit.</param>
/// <param name="MaxHeight">The tallest an image is left; a taller one is scaled down to fit. <see langword="null"/> sets no limit.</param>
/// <param name="Remove">Delete the image instead of recompressing it.</param>
/// <remarks>An image is fitted inside <paramref name="MaxWidth"/> by <paramref name="MaxHeight"/> with its aspect kept, and is never scaled up.</remarks>
public sealed record ImageOptimizeOptions(bool Enabled = false, int Quality = ImageOptimizeOptions.DefaultQuality, int? MaxWidth = null, int? MaxHeight = null, bool Remove = false)
{
    /// <summary>The default JPEG quality.</summary>
    public const int DefaultQuality = 80;

    /// <summary>The size album art is fitted inside.</summary>
    public const int AlbumArtSize = 512;

    /// <summary>The width a background image is fitted inside.</summary>
    public const int BackgroundWidth = 1920;

    /// <summary>The height a background image is fitted inside.</summary>
    public const int BackgroundHeight = 1080;

    /// <summary>Off, at quality 80, keeping the size.</summary>
    public static ImageOptimizeOptions Default { get; } = new();

    /// <summary>Off, at quality 80, album art fitted inside 512x512.</summary>
    public static ImageOptimizeOptions AlbumArtDefault { get; } = new(false, DefaultQuality, AlbumArtSize, AlbumArtSize);

    /// <summary>Off, at quality 80, a background image fitted inside 1920x1080.</summary>
    public static ImageOptimizeOptions BackgroundDefault { get; } = new(false, DefaultQuality, BackgroundWidth, BackgroundHeight);
}

/// <summary>What the Library Optimization Tool does. Every strategy is optional and configurable.</summary>
/// <param name="Metadata">Metadata cleanup.</param>
/// <param name="Audio">Audio transcoding, stem by stem.</param>
/// <param name="StemMixdown">Mixing a chart's stems down to one. Its bitrate is the mix's.</param>
/// <param name="Video">Background video recompression.</param>
/// <param name="AlbumArt">Album art recompression.</param>
/// <param name="BackgroundArt">Background image recompression.</param>
/// <param name="SavingsMargin">
/// The smallest saving, as a fraction of a file's size, that's worth acting on; a file saving less is skipped.
/// Metadata cleanup is exempt, as what it leaves out is tiny.
/// </param>
/// <param name="OutputFolder">
/// Where optimized charts are written, in the same layout; <see langword="null"/> optimizes in place. Only charts that
/// were changed are written there: one that's skipped (nothing to do, below the margin, in use, with unsaved changes) or
/// that fails is not copied, so the folder is not a full copy of the library.
/// </param>
/// <param name="Parallelism">
/// How many charts are worked on at once, by <see cref="OptimizeJob"/> and <see cref="OptimizationPlanner.PlanAllAsync"/>;
/// 1 does them one at a time. Videos take far longer than anything else, so this is mostly how many encode at once; the
/// video optimizer shares the machine's cores between those running. Below 1 counts as 1.
/// </param>
public sealed record OptimizeOptions(
    MetadataCleanupOptions Metadata,
    AudioOptimizeOptions Audio,
    AudioOptimizeOptions StemMixdown,
    VideoOptimizeOptions Video,
    ImageOptimizeOptions AlbumArt,
    ImageOptimizeOptions BackgroundArt,
    double SavingsMargin = 0.10,
    string? OutputFolder = null,
    int Parallelism = 1)
{
    /// <summary>A number of charts to work on at once that suits this machine: a quarter of its cores, at least 2 where it has them.</summary>
    public static int RecommendedParallelism => Math.Max(1, Math.Max(Environment.ProcessorCount >= 4 ? 2 : 1, Environment.ProcessorCount / 4));

    /// <summary>
    /// A number of charts to measure at once that suits this machine: all but one of its cores. Measuring audio and images
    /// takes one core each, so far more charts can be measured at once than encoded; videos, which take every core they're
    /// given, are held to <see cref="RecommendedParallelism"/> by <see cref="PlanServices.VideoGate"/>.
    /// </summary>
    public static int RecommendedMeasuringParallelism => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Every strategy off, in place, with a 10% margin.</summary>
    public static OptimizeOptions Default { get; } = new(
        MetadataCleanupOptions.Default,
        AudioOptimizeOptions.Default,
        AudioOptimizeOptions.Default,
        VideoOptimizeOptions.Default,
        ImageOptimizeOptions.AlbumArtDefault,
        ImageOptimizeOptions.BackgroundDefault);

    /// <summary>Whether <paramref name="strategy"/> is switched on.</summary>
    public bool IsEnabled(OptimizeStrategy strategy) => strategy switch
    {
        OptimizeStrategy.Metadata => Metadata.Enabled,
        OptimizeStrategy.Audio => Audio.Enabled,
        OptimizeStrategy.StemMixdown => StemMixdown.Enabled,
        OptimizeStrategy.Video => Video.Enabled,
        OptimizeStrategy.AlbumArt => AlbumArt.Enabled,
        OptimizeStrategy.BackgroundArt => BackgroundArt.Enabled,
        _ => false,
    };
}
