namespace ChartShared.Audio.Encoding;

/// <summary>How the export dialog's Opus transcoding sounds: the bitrate per stereo stem.</summary>
public enum OpusQuality
{
    /// <summary>64 kbps per stereo stem (32 kbps mono): small files, audibly lossy.</summary>
    Voice = 0,

    /// <summary>96 kbps per stereo stem (48 kbps mono): transparent enough for most stems.</summary>
    Balanced = 1,

    /// <summary>128 kbps per stereo stem (64 kbps mono): for stems worth keeping every detail of.</summary>
    Music = 2,

    /// <summary>192 kbps per stereo stem (96 kbps mono): near-lossless, large files.</summary>
    High = 3,
}

/// <summary>The bitrates behind <see cref="OpusQuality"/>, in bits per second.</summary>
public static class OpusBitrates
{
    /// <summary>The default quality the export dialog starts on.</summary>
    public const OpusQuality Default = OpusQuality.Music;

    /// <summary>The stereo bitrate of <paramref name="quality"/>.</summary>
    public static int StereoBps(OpusQuality quality) => quality switch
    {
        OpusQuality.Voice => 64000,
        OpusQuality.Balanced => 96000,
        OpusQuality.Music => 128000,
        OpusQuality.High => 192000,
        _ => throw new ArgumentOutOfRangeException(nameof(quality)),
    };

    /// <summary>The bitrate of <paramref name="quality"/> for <paramref name="channels"/>: mono uses half the stereo rate.</summary>
    public static int ForChannels(OpusQuality quality, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        return channels == 1 ? StereoBps(quality) / 2 : StereoBps(quality);
    }

    /// <summary>The quality closest to <paramref name="bitrateKbps"/>, as pack options carry it.</summary>
    public static OpusQuality Nearest(int bitrateKbps)
    {
        var qualities = Enum.GetValues<OpusQuality>();
        OpusQuality best = Default;
        int bestDistance = int.MaxValue;
        foreach (OpusQuality quality in qualities)
        {
            int distance = Math.Abs(StereoBps(quality) / 1000 - bitrateKbps);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = quality;
            }
        }

        return best;
    }
}
