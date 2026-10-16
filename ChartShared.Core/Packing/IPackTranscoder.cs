using ChartShared.Core.Jobs;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Packing;

/// <summary>Transcodes a pack plan's non-Opus audio stems to Opus before packing.</summary>
/// <remarks>
/// Defined here so <see cref="SongJob"/> can use it without referencing the audio
/// implementation (which itself references Core). Implemented in <c>ChartShared.Audio</c>.
/// </remarks>
public interface IPackTranscoder
{
    /// <summary>Whether transcoding can run in this build.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Transcodes every included audio stem that isn't already Opus into
    /// <paramref name="tempFolder"/> (created by the caller), and returns the plan's
    /// included files with those entries pointing at the transcoded output
    /// (<c>&lt;stem&gt;.opus</c>), in the same order.
    /// </summary>
    /// <param name="plan">The plan whose stems are transcoded.</param>
    /// <param name="tempFolder">An empty folder to write the <c>.opus</c> files into. Nothing is written elsewhere.</param>
    /// <param name="bitrateKbps">The Opus bitrate in kbps per stereo stem (mono stems use half).</param>
    /// <param name="progress">Receives <see cref="ExportPhase.Transcoding"/> progress; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels transcoding; partial output is left for the caller to delete.</param>
    /// <returns>The included files to pack instead of <see cref="PackPlan.Included"/>.</returns>
    /// <exception cref="InvalidDataException">A stem can't be decoded, or two stems would transcode to the same name.</exception>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Transcoding was cancelled.</exception>
    Task<IReadOnlyList<SngSourceFile>> TranscodeAsync(
        PackPlan plan,
        string tempFolder,
        int bitrateKbps,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken);
}
