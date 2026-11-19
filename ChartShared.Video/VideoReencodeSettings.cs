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

    /// <summary>A <c>cpu-used</c> that's used whatever <see cref="Speed"/> is: for measuring (tools/EncodeBench), and for estimates.</summary>
    internal int? CpuUsedOverride { get; init; }

    /// <summary>The <c>cpu-used</c> the encoder is given: <see cref="CpuUsedOverride"/>, or <see cref="Speed"/>'s.</summary>
    internal int EffectiveCpuUsed => CpuUsedOverride ?? CpuUsed;

    /// <summary>Encodes with libvpx's <c>deadline=realtime</c> rather than its default, <c>good</c>: much faster, and less efficient.</summary>
    internal bool Realtime { get; init; }

    /// <summary>How many threads the decoder uses; 0 or less leaves it to FFmpeg (one per core).</summary>
    internal int DecoderThreads { get; init; }

    /// <summary>How many threads the scaler uses; 1 or less is one, swscale's default.</summary>
    internal int ScalerThreads { get; init; }

    /// <summary>VP8's token partitions (FFmpeg's <c>-slices</c>: 1, 2, 4 or 8); 0 or less leaves libvpx's default, one.</summary>
    internal int Slices { get; init; }

    /// <summary>
    /// libvpx's <c>cpu-used</c> for <paramref name="speed"/>. Measured on real 480p, 1080p and 4K videos at CQ 32 (same CQ,
    /// so quality and size both move): from 0 to 1 the encode is about 20% faster, and costs about 5 to 10% more bits for
    /// the same picture; 2 and 3 cost about 1 to 3% more again and are about 1.5 times as fast as 1; 4 and 5 are twice and
    /// four times as fast as 1 and give up 10 to 20% in efficiency. libvpx's <c>deadline=best</c> was no better than 0.
    /// Measured again with tools/EncodeBench on 400 frames of two 1080p videos, 5 was three times as fast as 2, and at
    /// the same CQ its size was 0.90 and 1.34 of 2's.
    /// </summary>
    public static int CpuUsedOf(VideoEncodeSpeed speed) => speed switch
    {
        VideoEncodeSpeed.Fast => 4,
        VideoEncodeSpeed.Balanced => 2,
        VideoEncodeSpeed.Slow => 1,
        VideoEncodeSpeed.Best => 0,
        VideoEncodeSpeed.Fastest => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    /// <summary>The <c>cpu-used</c> the optimizer's estimates are encoded at, whatever the speed of the encode they're for: libvpx's quickest in its <c>good</c> mode.</summary>
    public const int EstimateCpuUsed = 5;

    /// <summary>
    /// What an estimate encoded at <see cref="EstimateCpuUsed"/> is multiplied by to give the size an encode at
    /// <paramref name="speed"/> writes.
    /// </summary>
    /// <remarks>
    /// Measured with tools/EncodeBench on six real 1080p videos (H.264 and VP8 sources) at CQ 32, against whole encodes
    /// at <see cref="VideoEncodeSpeed.Balanced"/>: the quick estimate took 0.28 of the time of one made at the encode's
    /// own speed, and came to 0.82 to 1.40 of the size written (1.05 on average), where the slow one came to 0.91 to 1.33
    /// (1.08 on average). Most of either's error is the windows not being the whole video; scaled by its average, the
    /// quick one was out by 19% on average and 33% at worst, the slow one by 15% and 23%. libvpx's realtime mode was no
    /// quicker for the same error until <c>cpu-used</c> 16, which was out by far more. The factors of the speeds other
    /// than Balanced weren't measured against whole encodes: Fast's is from estimates at its <c>cpu-used</c> on the same
    /// six (0.98 of the quick one's), Slow's and Best's from two 400-frame samples (Slow as Balanced, Best 5% smaller).
    /// </remarks>
    public static double EstimateFactor(VideoEncodeSpeed speed) => speed switch
    {
        VideoEncodeSpeed.Fastest => 1.0,
        VideoEncodeSpeed.Fast => 0.98,
        VideoEncodeSpeed.Balanced or VideoEncodeSpeed.Slow => 0.95,
        VideoEncodeSpeed.Best => 0.90,
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
