namespace ChartPacker.Core.Jobs;

/// <summary>What a pack with transcoding is doing while it reports progress.</summary>
public enum ExportPhase
{
    /// <summary>Non-Opus stems are being decoded, resampled and encoded to Opus.</summary>
    Transcoding,

    /// <summary>The <c>.sng</c> is being written (this includes its verify pass).</summary>
    Packing,
}

/// <summary>How far a pack with transcoding has got, for a determinate progress bar.</summary>
/// <param name="Phase">What it is doing.</param>
/// <param name="StemIndex">How many transcode items are done (transcoding), or 1 when packing is done.</param>
/// <param name="StemCount">How many transcode items there are (transcoding), or 1.</param>
/// <param name="CurrentStem">The stem being transcoded, or the <c>.sng</c> file name while packing.</param>
/// <param name="Fraction">How far the phase has got, from 0 to 1.</param>
public sealed record ExportProgress(ExportPhase Phase, int StemIndex, int StemCount, string CurrentStem, double Fraction);
