using ChartShared.Core.Dedup;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>
/// Classifies the files directly in a chart, a song folder or a <c>.sng</c>, and probes the ones an optimizer could
/// shrink for their codec, dimensions and duration.
/// </summary>
public static class AssetInspector
{
    /// <summary>The stem of <c>highway.*</c> images, which are always left alone.</summary>
    public const string HighwayStem = "highway";

    /// <summary>What the file <paramref name="storedName"/>, directly in a chart, is.</summary>
    public static AssetKind Classify(string storedName)
    {
        ArgumentNullException.ThrowIfNull(storedName);

        if (storedName.Contains('/') || storedName.Contains('\\'))
            return AssetKind.Other;
        if (storedName.Equals(Ini.SongIni.FileName, StringComparison.OrdinalIgnoreCase))
            return AssetKind.SongIni;
        if (ChartFiles.IsChartName(storedName))
            return AssetKind.Chart;
        if (AudioStems.StemOf(storedName) is not null)
            return AssetKind.Audio;
        if (AlbumArt.IsAlbumArtName(storedName))
            return AssetKind.AlbumArt;
        if (SongBackgrounds.KindOfName(storedName) is { } kind)
            return kind == BackgroundKind.Video ? AssetKind.Video : AssetKind.BackgroundImage;
        if (Path.GetFileNameWithoutExtension(storedName).Equals(HighwayStem, StringComparison.OrdinalIgnoreCase)
            && SongBackgrounds.ImageExtensions.Contains(Path.GetExtension(storedName), StringComparer.OrdinalIgnoreCase))
            return AssetKind.HighwayImage;
        return AssetKind.Other;
    }

    /// <summary>
    /// Lists and classifies the files directly in <paramref name="entry"/>, and probes its audio, video and images with
    /// <paramref name="probes"/>. A file a probe can't read is left unprobed, not an error.
    /// </summary>
    /// <exception cref="IOException">The folder or <c>.sng</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The inspection was cancelled.</exception>
    public static Task<ChartAssets> InspectAsync(SongEntry entry, AssetProbes? probes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return InspectCoreAsync(entry, probes ?? AssetProbes.None, cancellationToken);
    }

    private static async Task<ChartAssets> InspectCoreAsync(SongEntry entry, AssetProbes probes, CancellationToken cancellationToken)
    {
        var stamp = await Task.Run(() => EntryStamps.Capture(entry.FullPath, entry.Kind, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var reader = await Task.Run(() => SongFileReader.Open(entry, cancellationToken), cancellationToken).ConfigureAwait(false);

        var assets = new List<ChartAsset>();
        foreach (var file in reader.TopLevel.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = Classify(file.Name);
            string extension = Path.GetExtension(file.Name).ToLowerInvariant();
            string? stem = kind == AssetKind.Audio ? AudioStems.StemOf(file.Name) : null;
            var probe = await ProbeAsync(reader, file.Name, kind, probes, cancellationToken).ConfigureAwait(false);
            assets.Add(new ChartAsset(file.Name, extension, file.Length, kind, stem, probe));
        }

        return new ChartAssets(entry, assets, stamp);
    }

    private static async Task<ProbeResult?> ProbeAsync(SongFileReader reader, string name, AssetKind kind, AssetProbes probes, CancellationToken cancellationToken)
    {
        Func<Stream, Task<ProbeResult?>>? probe = kind switch
        {
            AssetKind.Audio when probes.Audio is { } audio => s => audio.ProbeAsync(name, s, cancellationToken),
            AssetKind.Video when probes.Video is { } video => s => video.ProbeAsync(name, s, cancellationToken),
            AssetKind.AlbumArt or AssetKind.BackgroundImage when probes.Image is { } image => s => image.ProbeAsync(name, s, cancellationToken),
            _ => null,
        };
        if (probe is null)
            return null;

        try
        {
            await using var stream = reader.OpenRead(name);
            return stream is null ? null : await probe(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
