using ChartShared.Core.Optimization;

namespace ChartShared.Core.Settings;

/// <summary>
/// What the Library Optimization Tool starts from: the strategies it has switched on and how each is set. Persisted in
/// <c>optimizer-settings.json</c> by <see cref="OptimizeSettingsFile"/>; <see cref="ToOptions"/> turns it into the
/// <see cref="OptimizeOptions"/> a run takes.
/// </summary>
/// <remarks>
/// A flat, mutable class, not <see cref="OptimizeOptions"/>' records, so the file has one property per setting and a
/// missing one reads as its default. Every strategy is off until the user turns it on; the window keeps what it was
/// last set to.
/// </remarks>
public sealed class OptimizeSettings
{
    /// <summary>The lowest Opus bitrate, per stereo stem, the tool offers.</summary>
    public const int MinBitrateKbps = 16;

    /// <summary>The highest Opus bitrate, per stereo stem, the tool offers.</summary>
    public const int MaxBitrateKbps = 320;

    /// <summary>The lowest CQ libvpx takes.</summary>
    public const int MinCq = 4;

    /// <summary>The highest CQ libvpx takes.</summary>
    public const int MaxCq = 63;

    /// <summary>The smallest size limit an image is fitted inside.</summary>
    public const int MinImageSize = 16;

    /// <summary>The largest size limit an image is fitted inside.</summary>
    public const int MaxImageSize = 16384;

    /// <summary>The defaults as shipped: every strategy off, the recommended settings for each.</summary>
    public static OptimizeSettings Shipped => new();

    /// <summary>Whether metadata cleanup runs.</summary>
    public bool MetadataEnabled { get; set; }

    /// <summary>Drop the <c>song.ini</c> keys of parts the chart has no notes for.</summary>
    public bool PruneEmptyParts { get; set; } = true;

    /// <summary>Drop the tempo anchors only Chart Builder reads from <c>notes.mid</c>.</summary>
    public bool StripEditorEvents { get; set; } = true;

    /// <summary>Drop keys with an empty value.</summary>
    public bool DropEmptyValues { get; set; } = true;

    /// <summary>Rename legacy keys to their replacements.</summary>
    public bool ReplaceLegacyKeys { get; set; } = true;

    /// <summary>Whether audio is transcoded to Opus.</summary>
    public bool AudioEnabled { get; set; }

    /// <summary>The Opus bitrate per stereo stem, in kbps.</summary>
    public int AudioBitrateKbps { get; set; } = AudioOptimizeOptions.DefaultBitrateKbps;

    /// <summary>Whether a chart's stems are mixed down to one.</summary>
    public bool StemMixdownEnabled { get; set; }

    /// <summary>The bitrate of the mix, in kbps.</summary>
    public int StemMixdownBitrateKbps { get; set; } = AudioOptimizeOptions.DefaultBitrateKbps;

    /// <summary>Whether background video is recompressed.</summary>
    public bool VideoEnabled { get; set; }

    /// <summary>The size a video is brought down to.</summary>
    public VideoTarget VideoTarget { get; set; } = VideoTarget.Fhd;

    /// <summary>libvpx's constant-quality value, <see cref="MinCq"/> to <see cref="MaxCq"/>.</summary>
    public int VideoCq { get; set; } = VideoOptimizeOptions.DefaultCq;

    /// <summary>Also convert videos in a codec the games can't all play, whatever it saves.</summary>
    public bool VideoConvertUnsupported { get; set; }

    /// <summary>How hard the video encoder works.</summary>
    public VideoEncodeSpeed VideoSpeed { get; set; } = VideoEncodeSpeed.Balanced;

    /// <summary>Whether album art is recompressed.</summary>
    public bool AlbumArtEnabled { get; set; }

    /// <summary>The JPEG quality of album art, 1 to 100.</summary>
    public int AlbumArtQuality { get; set; } = ImageOptimizeOptions.DefaultQuality;

    /// <summary>The widest album art is left; null for no limit.</summary>
    public int? AlbumArtMaxWidth { get; set; } = ImageOptimizeOptions.AlbumArtSize;

    /// <summary>The tallest album art is left; null for no limit.</summary>
    public int? AlbumArtMaxHeight { get; set; } = ImageOptimizeOptions.AlbumArtSize;

    /// <summary>Whether background images are recompressed.</summary>
    public bool BackgroundArtEnabled { get; set; }

    /// <summary>The JPEG quality of background images, 1 to 100.</summary>
    public int BackgroundArtQuality { get; set; } = ImageOptimizeOptions.DefaultQuality;

    /// <summary>The widest a background image is left; null for no limit.</summary>
    public int? BackgroundArtMaxWidth { get; set; } = ImageOptimizeOptions.BackgroundWidth;

    /// <summary>The tallest a background image is left; null for no limit.</summary>
    public int? BackgroundArtMaxHeight { get; set; } = ImageOptimizeOptions.BackgroundHeight;

    /// <summary>The smallest saving, as a fraction of a file's size, worth acting on; 0 to 0.9.</summary>
    public double SavingsMargin { get; set; } = 0.10;

    /// <summary>
    /// Whether optimized charts are written to <see cref="OutputFolder"/>, leaving the library as it is. On as shipped, so
    /// the originals are only overwritten when the user chooses that.
    /// </summary>
    public bool UseOutputFolder { get; set; } = true;

    /// <summary>The folder optimized charts were last written to; null when none has been chosen.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>How many charts are worked on at once; 0 takes <see cref="OptimizeOptions.RecommendedParallelism"/>.</summary>
    public int Parallelism { get; set; }

    /// <summary>A copy with every value kept to what it can be.</summary>
    public OptimizeSettings Normalized() => new()
    {
        MetadataEnabled = MetadataEnabled,
        PruneEmptyParts = PruneEmptyParts,
        StripEditorEvents = StripEditorEvents,
        DropEmptyValues = DropEmptyValues,
        ReplaceLegacyKeys = ReplaceLegacyKeys,
        AudioEnabled = AudioEnabled,
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, MinBitrateKbps, MaxBitrateKbps),
        StemMixdownEnabled = StemMixdownEnabled,
        StemMixdownBitrateKbps = Math.Clamp(StemMixdownBitrateKbps, MinBitrateKbps, MaxBitrateKbps),
        VideoEnabled = VideoEnabled,
        VideoTarget = Enum.IsDefined(VideoTarget) ? VideoTarget : VideoTarget.Fhd,
        VideoCq = Math.Clamp(VideoCq, MinCq, MaxCq),
        VideoConvertUnsupported = VideoConvertUnsupported,
        VideoSpeed = Enum.IsDefined(VideoSpeed) ? VideoSpeed : VideoEncodeSpeed.Balanced,
        AlbumArtEnabled = AlbumArtEnabled,
        AlbumArtQuality = ImageOptimization.ClampQuality(AlbumArtQuality),
        AlbumArtMaxWidth = Limit(AlbumArtMaxWidth),
        AlbumArtMaxHeight = Limit(AlbumArtMaxHeight),
        BackgroundArtEnabled = BackgroundArtEnabled,
        BackgroundArtQuality = ImageOptimization.ClampQuality(BackgroundArtQuality),
        BackgroundArtMaxWidth = Limit(BackgroundArtMaxWidth),
        BackgroundArtMaxHeight = Limit(BackgroundArtMaxHeight),
        SavingsMargin = double.IsFinite(SavingsMargin) ? Math.Clamp(SavingsMargin, 0, 0.9) : 0.10,
        UseOutputFolder = UseOutputFolder,
        OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder.Trim(),
        Parallelism = Math.Clamp(Parallelism, 0, 64),
    };

    /// <summary>The options a run takes, writing into <paramref name="outputFolder"/>, or in place when it's null.</summary>
    public OptimizeOptions ToOptions(string? outputFolder)
    {
        var s = Normalized();
        return new OptimizeOptions(
            new MetadataCleanupOptions(s.MetadataEnabled, s.PruneEmptyParts, s.StripEditorEvents, s.DropEmptyValues, s.ReplaceLegacyKeys),
            new AudioOptimizeOptions(s.AudioEnabled, s.AudioBitrateKbps),
            new AudioOptimizeOptions(s.StemMixdownEnabled, s.StemMixdownBitrateKbps),
            new VideoOptimizeOptions(s.VideoEnabled, s.VideoTarget, s.VideoCq, Remove: false, s.VideoConvertUnsupported, s.VideoSpeed),
            new ImageOptimizeOptions(s.AlbumArtEnabled, s.AlbumArtQuality, s.AlbumArtMaxWidth, s.AlbumArtMaxHeight),
            new ImageOptimizeOptions(s.BackgroundArtEnabled, s.BackgroundArtQuality, s.BackgroundArtMaxWidth, s.BackgroundArtMaxHeight),
            s.SavingsMargin,
            string.IsNullOrWhiteSpace(outputFolder) ? null : outputFolder,
            s.Parallelism > 0 ? s.Parallelism : OptimizeOptions.RecommendedParallelism);
    }

    private static int? Limit(int? size) => size is { } v ? Math.Clamp(v, MinImageSize, MaxImageSize) : null;
}
