using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>
/// Puts a chart's files where an optimizer can read them: a folder's own files in place, a <c>.sng</c>'s extracted,
/// unmasked, into a staging folder that's deleted when this is disposed.
/// </summary>
internal sealed class AssetStager(SongEntry entry, Func<string> stagingFolder) : IDisposable
{
    private readonly Dictionary<string, AssetFile> _staged = new(StringComparer.OrdinalIgnoreCase);
    private string? _folder;

    /// <summary>The staging folder, once something has been extracted; <see langword="null"/> before.</summary>
    public string? Folder => _folder;

    /// <summary>Where <paramref name="asset"/> can be read.</summary>
    /// <exception cref="FileNotFoundException">The file is no longer in the chart.</exception>
    /// <exception cref="IOException">The file can't be read or extracted.</exception>
    public async Task<AssetFile> GetAsync(ChartAsset asset, CancellationToken cancellationToken)
    {
        if (entry.Kind != SongEntryKind.SngFile)
            return new AssetFile(asset, Path.Combine(entry.FullPath, asset.StoredName));

        if (_staged.TryGetValue(asset.StoredName, out var existing))
            return existing;

        _folder ??= Directory.CreateDirectory(stagingFolder()).FullName;
        string path = Path.Combine(_folder, "src-" + asset.StoredName);
        await Task.Run(() =>
        {
            try
            {
                // Opened per file, so the .sng isn't held open while it's rewritten.
                using var reader = SongFileReader.Open(entry, cancellationToken);
                using var source = reader.OpenRead(asset.StoredName)
                    ?? throw new FileNotFoundException($"'{asset.StoredName}' is no longer in the chart.", asset.StoredName);
                using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                source.CopyTo(target);
            }
            catch
            {
                // A partial file isn't cached, so it would make the next try at this asset fail on CreateNew.
                StagingPath.TryDeleteFile(path);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        var file = new AssetFile(asset, path);
        _staged[asset.StoredName] = file;
        return file;
    }

    public void Dispose()
    {
        if (_folder is not null)
            StagingPath.TryDeleteDirectory(_folder);
        _folder = null;
        _staged.Clear();
    }

    /// <summary>A stager that extracts into a new folder under the temp folder, so a library is never written to.</summary>
    public static AssetStager InTemp(SongEntry entry) =>
        new(entry, () => Path.Combine(Path.GetTempPath(), "chartpacker-opt-" + Guid.NewGuid().ToString("N")));
}
