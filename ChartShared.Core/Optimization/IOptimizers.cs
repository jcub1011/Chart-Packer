namespace ChartShared.Core.Optimization;

/// <summary>Reads an audio file's codec and duration. Implemented by <c>ChartShared.Audio</c>.</summary>
public interface IAudioProbe
{
    /// <summary>Probes <paramref name="stream"/>, a seekable stream of the file <paramref name="name"/>.</summary>
    /// <returns>What it learned, or <see langword="null"/> when the file isn't one it can read.</returns>
    Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken);
}

/// <summary>Reads a video's codec, dimensions and duration. Implemented by <c>ChartShared.Video</c>.</summary>
public interface IVideoProbe
{
    /// <inheritdoc cref="IAudioProbe.ProbeAsync"/>
    Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken);
}

/// <summary>Reads an image's format and dimensions. Implemented by <c>ChartShared.UI</c>.</summary>
public interface IImageProbe
{
    /// <inheritdoc cref="IAudioProbe.ProbeAsync"/>
    Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken);
}

/// <summary>The probes <see cref="AssetInspector"/> uses; one that's null leaves its files unprobed.</summary>
public sealed record AssetProbes(IAudioProbe? Audio = null, IVideoProbe? Video = null, IImageProbe? Image = null)
{
    /// <summary>No probes.</summary>
    public static AssetProbes None { get; } = new();
}

/// <summary>What an optimizer expects optimizing a file to give.</summary>
/// <param name="OutputBytes">The estimated size of the output.</param>
/// <param name="OutputExtension">The output's extension, with the dot, as <c>.opus</c>; it names the file that's written.</param>
/// <param name="AlreadyOptimal">The file is already as the settings would leave it, as Opus already is; nothing is written.</param>
public sealed record OptimizeEstimate(long OutputBytes, string OutputExtension, bool AlreadyOptimal = false);

/// <summary>Transcodes audio, or mixes several stems into one. Implemented by <c>ChartShared.Audio</c>.</summary>
public interface IAudioOptimizer
{
    /// <summary>Whether it can run here.</summary>
    bool IsAvailable { get; }

    /// <summary>What optimizing <paramref name="sources"/> would give, without writing anything the caller keeps.</summary>
    /// <param name="sources">One stem to transcode, or several to mix into one.</param>
    /// <exception cref="IOException">A source can't be read.</exception>
    /// <exception cref="InvalidDataException">A source can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    Task<OptimizeEstimate> EstimateAsync(IReadOnlyList<AssetFile> sources, AudioOptimizeOptions options, CancellationToken cancellationToken);

    /// <summary>Writes the optimized <paramref name="sources"/> to <paramref name="destinationPath"/>.</summary>
    /// <param name="progress">Reports 0 to 1.</param>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="InvalidDataException">A source can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled; a partial output may be left for the caller to delete.</exception>
    Task OptimizeAsync(IReadOnlyList<AssetFile> sources, string destinationPath, AudioOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Recompresses a background video. Implemented by <c>ChartShared.Video</c>.</summary>
public interface IVideoOptimizer
{
    /// <inheritdoc cref="IAudioOptimizer.IsAvailable"/>
    bool IsAvailable { get; }

    /// <summary>What optimizing <paramref name="source"/> would give.</summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="InvalidDataException">The file can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    Task<OptimizeEstimate> EstimateAsync(AssetFile source, VideoOptimizeOptions options, CancellationToken cancellationToken);

    /// <summary>Writes the optimized <paramref name="source"/> to <paramref name="destinationPath"/>.</summary>
    /// <param name="progress">Reports 0 to 1.</param>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="InvalidDataException">The file can't be decoded.</exception>
    /// <exception cref="OutputTooLargeException">
    /// What it was writing had already passed the size of <paramref name="source"/>, so it stopped; nothing is written. Not
    /// thrown for a conversion for compatibility, which is kept whatever its size.
    /// </exception>
    /// <exception cref="OperationCanceledException">It was cancelled; a partial output may be left for the caller to delete.</exception>
    Task OptimizeAsync(AssetFile source, string destinationPath, VideoOptimizeOptions options, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// An optimizer stopped part-way because its output had already grown past the file it would replace: carrying on could
/// only write a file that wouldn't be kept. <see cref="OptimizeJob"/> leaves the file as it was, as <see cref="OptimizeSkipReason.WouldGrow"/>.
/// </summary>
public sealed class OutputTooLargeException(string message) : Exception(message);

/// <summary>Recompresses album art or a background image. Implemented by <c>ChartShared.UI</c>.</summary>
public interface IImageOptimizer
{
    /// <inheritdoc cref="IAudioOptimizer.IsAvailable"/>
    bool IsAvailable { get; }

    /// <summary>What optimizing <paramref name="source"/> would give.</summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="InvalidDataException">The file can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">The estimate was cancelled.</exception>
    Task<OptimizeEstimate> EstimateAsync(AssetFile source, ImageOptimizeOptions options, CancellationToken cancellationToken);

    /// <summary>Writes the optimized <paramref name="source"/> to <paramref name="destinationPath"/>.</summary>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="InvalidDataException">The file can't be decoded.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled; a partial output may be left for the caller to delete.</exception>
    Task OptimizeAsync(AssetFile source, string destinationPath, ImageOptimizeOptions options, CancellationToken cancellationToken);
}

/// <summary>The optimizers a run uses. One that's null, or not <c>IsAvailable</c>, leaves its files alone.</summary>
public sealed record OptimizerSet(IAudioOptimizer? Audio = null, IVideoOptimizer? Video = null, IImageOptimizer? Image = null)
{
    /// <summary>No optimizers.</summary>
    public static OptimizerSet None { get; } = new();
}
