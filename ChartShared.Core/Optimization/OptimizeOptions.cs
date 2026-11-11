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
/// <param name="BitrateKbps">The bitrate of the Opus it writes, per file.</param>
public sealed record AudioOptimizeOptions(bool Enabled = false, int BitrateKbps = 128)
{
    /// <summary>Off, at 128 kbps.</summary>
    public static AudioOptimizeOptions Default { get; } = new();
}

/// <summary>Video recompression settings.</summary>
/// <param name="Enabled">Whether the strategy runs.</param>
/// <param name="Target">The size the video is brought down to.</param>
/// <param name="Cq">The constant-quality value; lower is better quality and a larger file.</param>
/// <param name="Remove">Delete the video instead of recompressing it.</param>
public sealed record VideoOptimizeOptions(bool Enabled = false, VideoTarget Target = VideoTarget.Fhd, int Cq = 30, bool Remove = false)
{
    /// <summary>Off, FHD at CQ 30.</summary>
    public static VideoOptimizeOptions Default { get; } = new();
}

/// <summary>Image recompression settings, for album art or the background image.</summary>
/// <param name="Enabled">Whether the strategy runs.</param>
/// <param name="Quality">The encoder quality, 1 to 100.</param>
/// <param name="MaxDimension">The longest side an image is scaled down to; <see langword="null"/> keeps its size.</param>
/// <param name="Remove">Delete the image instead of recompressing it.</param>
public sealed record ImageOptimizeOptions(bool Enabled = false, int Quality = 85, int? MaxDimension = null, bool Remove = false)
{
    /// <summary>Off, at quality 85, keeping the size.</summary>
    public static ImageOptimizeOptions Default { get; } = new();
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
public sealed record OptimizeOptions(
    MetadataCleanupOptions Metadata,
    AudioOptimizeOptions Audio,
    AudioOptimizeOptions StemMixdown,
    VideoOptimizeOptions Video,
    ImageOptimizeOptions AlbumArt,
    ImageOptimizeOptions BackgroundArt,
    double SavingsMargin = 0.10,
    string? OutputFolder = null)
{
    /// <summary>Every strategy off, in place, with a 10% margin.</summary>
    public static OptimizeOptions Default { get; } = new(
        MetadataCleanupOptions.Default,
        AudioOptimizeOptions.Default,
        AudioOptimizeOptions.Default,
        VideoOptimizeOptions.Default,
        ImageOptimizeOptions.Default,
        ImageOptimizeOptions.Default);

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
