using ChartShared.Core.Optimization;

namespace ChartShared.Video;

/// <summary>How <see cref="VideoReencoder"/> encodes: the size a video is fitted inside, and libvpx's quality.</summary>
/// <param name="Target">The size the video is fitted inside; it's never scaled up.</param>
/// <param name="Cq">
/// libvpx's constant-quality level, <see cref="MinCq"/> to <see cref="MaxCq"/>; lower is better quality and a larger file.
/// This is FFmpeg's <c>-crf</c> for VP8, not the 0 to 100 scale some editors use.
/// </param>
/// <param name="MaxBitrateKbps">The most the encoder may spend, in kilobits per second; 0 or less takes <see cref="DefaultMaxBitrateKbps"/> for the target.</param>
/// <param name="Speed">How hard the encoder works: slower gives a smaller file, or a better picture, for the same CQ.</param>
/// <param name="Threads">How many threads the encoder uses; 0 or less takes the machine's cores, up to 8.</param>
public sealed record VideoReencodeSettings(
    VideoTarget Target = VideoTarget.Fhd, int Cq = VideoReencodeSettings.DefaultCq, int MaxBitrateKbps = 0,
    VideoEncodeSpeed Speed = VideoEncodeSpeed.Balanced, int Threads = 0)
{
    /// <summary>The default quality level: the FFmpeg wiki's starting point for libvpx VP8.</summary>
    public const int DefaultCq = VideoOptimizeOptions.DefaultCq;

    /// <summary>The best quality libvpx's CQ takes.</summary>
    public const int MinCq = 4;

    /// <summary>The worst quality libvpx's CQ takes.</summary>
    public const int MaxCq = 63;

    /// <summary>
    /// How many frames apart keyframes are (FFmpeg's <c>-g</c>). A sampled estimate encodes windows of this many frames,
    /// so each carries the one keyframe a whole encode has per this many.
    /// </summary>
    public const int GopFrames = 128;

    /// <summary>The widest an SD video is left.</summary>
    public const int SdWidth = 854;

    /// <summary>The tallest an SD video is left.</summary>
    public const int SdHeight = 480;

    /// <summary>The widest an HD video is left.</summary>
    public const int HdWidth = 1280;

    /// <summary>The tallest an HD video is left.</summary>
    public const int HdHeight = 720;

    /// <summary>The widest a QHD video is left.</summary>
    public const int QhdWidth = 2560;

    /// <summary>The tallest a QHD video is left.</summary>
    public const int QhdHeight = 1440;

    /// <summary>The widest an FHD video is left.</summary>
    public const int FhdWidth = 1920;

    /// <summary>The tallest an FHD video is left.</summary>
    public const int FhdHeight = 1080;

    /// <summary>The widest a UHD video is left.</summary>
    public const int UhdWidth = 3840;

    /// <summary>The tallest a UHD video is left.</summary>
    public const int UhdHeight = 2160;

    // Chosen from estimates of 11 real videos (H.264, VP8 and AV1, 480p to 4K) at CQ 10: uncapped, 1080p came out at about
    // 7 Mbps whatever the source's rate, and a 4 Mbps cap held it to about 4 Mbps (a 4K source of 7 to 8 Mbps went to about
    // half its size) where 8 Mbps barely changed anything. At the default CQ 32, 1080p comes out at about 4 Mbps by itself,
    // so the cap only bites for demanding content or a lower CQ. The UHD cap is that scaled by about three rather than the pixel
    // count's four, and wasn't measured. The SD, HD and QHD caps are scaled from FHD's by the same rule, and weren't measured
    // either.
    /// <summary>The bitrate cap an SD encode has when none is given, in kilobits per second.</summary>
    public const int SdMaxBitrateKbps = 1000;

    /// <summary>The bitrate cap an HD encode has when none is given, in kilobits per second.</summary>
    public const int HdMaxBitrateKbps = 2000;

    /// <summary>The bitrate cap a QHD encode has when none is given, in kilobits per second.</summary>
    public const int QhdMaxBitrateKbps = 6500;

    /// <summary>The bitrate cap an FHD encode has when none is given, in kilobits per second.</summary>
    public const int FhdMaxBitrateKbps = 4000;

    /// <summary>The bitrate cap a UHD encode has when none is given, in kilobits per second.</summary>
    public const int UhdMaxBitrateKbps = 12000;

    /// <summary>libvpx's <c>cpu-used</c> for <see cref="Speed"/>: 0 is its slowest and best.</summary>
    public int CpuUsed => CpuUsedOf(Speed);

    /// <summary>
    /// libvpx's <c>cpu-used</c> for <paramref name="speed"/>. Measured on real 480p, 1080p and 4K videos at CQ 32 (same CQ,
    /// so quality and size both move): from 0 to 1 the encode is about 20% faster, and costs about 5 to 10% more bits for
    /// the same picture; 2 and 3 cost about 1 to 3% more again and are about 1.5 times as fast as 1; 4 and 5 are twice and
    /// four times as fast as 1 and give up 10 to 20% in efficiency. libvpx's <c>deadline=best</c> was no better than 0.
    /// </summary>
    public static int CpuUsedOf(VideoEncodeSpeed speed) => speed switch
    {
        VideoEncodeSpeed.Fast => 4,
        VideoEncodeSpeed.Balanced => 2,
        VideoEncodeSpeed.Slow => 1,
        VideoEncodeSpeed.Best => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    /// <summary>The cap that applies: <see cref="MaxBitrateKbps"/>, or the target's default.</summary>
    public int EffectiveMaxBitrateKbps => MaxBitrateKbps > 0 ? MaxBitrateKbps : DefaultMaxBitrateKbps(Target);

    /// <summary>The size <see cref="Target"/> fits a video inside.</summary>
    public (int Width, int Height) MaxSize => Target switch
    {
        VideoTarget.Sd => (SdWidth, SdHeight),
        VideoTarget.Hd => (HdWidth, HdHeight),
        VideoTarget.Fhd => (FhdWidth, FhdHeight),
        VideoTarget.Qhd => (QhdWidth, QhdHeight),
        VideoTarget.Uhd => (UhdWidth, UhdHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(Target), Target, null),
    };

    /// <summary>The default bitrate cap of <paramref name="target"/>, in kilobits per second.</summary>
    public static int DefaultMaxBitrateKbps(VideoTarget target) => target switch
    {
        VideoTarget.Sd => SdMaxBitrateKbps,
        VideoTarget.Hd => HdMaxBitrateKbps,
        VideoTarget.Fhd => FhdMaxBitrateKbps,
        VideoTarget.Qhd => QhdMaxBitrateKbps,
        VideoTarget.Uhd => UhdMaxBitrateKbps,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// <summary>Settings for what the Library Optimization Tool was asked for.</summary>
    public static VideoReencodeSettings From(VideoOptimizeOptions options) => new(options.Target, options.Cq, Speed: options.Speed);

    /// <exception cref="ArgumentOutOfRangeException"><see cref="Cq"/> is outside libvpx's range.</exception>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Cq, MinCq, nameof(Cq));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Cq, MaxCq, nameof(Cq));
        _ = CpuUsedOf(Speed);
    }
}
