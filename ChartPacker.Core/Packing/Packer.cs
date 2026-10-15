using ChartPacker.Core.Sng;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Packing;

/// <summary>Executes a <see cref="PackPlan"/> (project spec F1.6–F1.7).</summary>
public static class Packer
{
    /// <summary>
    /// Writes <paramref name="plan"/> to <paramref name="targetPath"/> with a fresh random mask,
    /// then re-reads it structurally before renaming it into place. Its <c>notes.mid</c> is stored
    /// without the events only Chart Builder reads (<see cref="ExportCleanup"/>); the folder's is left alone.
    /// </summary>
    /// <returns>The verify pass's findings. Any error means the target was not written.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="plan"/> has errors.</exception>
    /// <exception cref="IOException">A file can't be read or written, or the target exists and <paramref name="overwrite"/> is false.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Packing was cancelled; nothing is left behind.</exception>
    public static async Task<IReadOnlyList<Finding>> PackAsync(
        PackPlan plan,
        string targetPath,
        bool overwrite,
        IProgress<long>? bytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (plan.HasErrors)
            throw new InvalidOperationException("A plan with errors can't be packed.");

        if (Path.GetDirectoryName(Path.GetFullPath(targetPath)) is { } parent)
            Directory.CreateDirectory(parent);

        var (included, staging) = await ExportCleanup.StripChartAsync(plan.Included, cancellationToken).ConfigureAwait(false);
        try
        {
            return await SngWriter.WriteAsync(
                targetPath,
                SngMask.Generate(),
                plan.Metadata,
                included,
                overwrite,
                verify: true,
                bytesWritten,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (staging is not null)
                ExportCleanup.TryDeleteFolder(staging);
        }
    }
}
