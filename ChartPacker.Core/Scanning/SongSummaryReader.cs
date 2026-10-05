using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Core.Scanning;

/// <summary>What the library list shows for a song: its stored metadata, its files and chart, and how big it is.</summary>
/// <param name="Pairs">The stored metadata pairs in order; empty when a folder has no readable <c>[song]</c> section.</param>
/// <param name="TotalSize">The <c>.sng</c> file's size, or the sum of every file in the folder, in bytes.</param>
/// <param name="StoredNames">The names of the files directly in the song, as stored.</param>
/// <param name="Chart">
/// What the song's chart has; <see cref="ChartContents.None"/> when it has no chart, and
/// <see langword="null"/> when the chart can't be read or is larger than <see cref="ChartReader.MaxBytes"/>.
/// </param>
/// <param name="FallbackLengthMs">
/// The longest audio file's length in milliseconds, measured only when <paramref name="Pairs"/> has
/// no usable <c>song_length</c>; otherwise <see langword="null"/>.
/// </param>
public sealed record SongSummary(
    IReadOnlyList<KeyValuePair<string, string>> Pairs,
    ulong TotalSize,
    IReadOnlyList<string> StoredNames,
    ChartContents? Chart,
    long? FallbackLengthMs = null);

/// <summary>Reads a <see cref="SongSummary"/> for a library entry.</summary>
public static class SongSummaryReader
{
    /// <summary>Reads <paramref name="entry"/>'s summary on the thread pool.</summary>
    /// <remarks>
    /// A <c>.sng</c> with structural problems still gives whatever metadata could be read. A folder's
    /// metadata comes from the <c>song.ini</c> directly in it, matched ignoring case. When the
    /// metadata has no usable <c>song_length</c>, the audio files are measured with
    /// <see cref="AudioLength"/>. The chart <see cref="ChartReader.Pick"/> picks is read with
    /// <see cref="ChartContents.Read"/>.
    /// </remarks>
    /// <exception cref="IOException">The file or folder can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<SongSummary> ReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => Read(entry, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but <see langword="null"/> when the song can't be read, for whatever reason: a
    /// malformed file can fail in ways other than an IO error, and one song shouldn't stop a library scan.
    /// </summary>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<SongSummary?> TryReadAsync(SongEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => TryRead(entry, cancellationToken), cancellationToken);
    }

    /// <summary>Like <see cref="TryReadAsync"/>, blocking the calling thread.</summary>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal static SongSummary? TryRead(SongEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return Read(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Like <see cref="ReadAsync"/>, blocking the calling thread.</summary>
    /// <inheritdoc cref="ReadAsync" path="/exception"/>
    internal static SongSummary Read(SongEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return entry.Kind == SongEntryKind.SngFile ? ReadSng(entry.FullPath, cancellationToken) : ReadFolder(entry.FullPath, cancellationToken);
    }

    /// <remarks>Everything is read through one handle: on a hard drive each open and each read can cost a seek.</remarks>
    private static SongSummary ReadSng(string path, CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = SngReader.Read(handle, cancellationToken);
        var pairs = result.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();
        ulong size = (ulong)RandomAccess.GetLength(handle);
        var names = result.Files.Select(f => f.Name).Where(n => !n.Contains('/')).ToList();
        var chart = ReadSngChart(handle, result, pairs, cancellationToken);
        return HasLength(pairs)
            ? new SongSummary(pairs, size, names, chart)
            : new SongSummary(pairs, size, names, chart, AudioLength.LongestInSng(handle, result, cancellationToken));
    }

    private static SongSummary ReadFolder(string folder, CancellationToken cancellationToken)
    {
        var files = SongFolderListing.List(folder, cancellationToken);
        ulong size = (ulong)files.Sum(f => f.Length);
        var storedNames = files.Select(f => f.RelativePath).Where(n => !n.Contains('/')).ToList();

        var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<KeyValuePair<string, string>> pairs = songIni is null
            ? []
            : SongIni.Read(Path.Combine(folder, songIni.RelativePath), cancellationToken).Pairs;
        var chart = ReadFolderChart(folder, files, pairs, cancellationToken);

        return HasLength(pairs)
            ? new SongSummary(pairs, size, storedNames, chart)
            : new SongSummary(pairs, size, storedNames, chart, AudioLength.LongestInFolder(folder, files, cancellationToken));
    }

    private static ChartContents? ReadSngChart(SafeFileHandle sng, SngReadResult result, IReadOnlyList<KeyValuePair<string, string>> pairs, CancellationToken cancellationToken)
    {
        if (result.Header is not { } header)
            return null;

        var file = ChartReader.Pick(result.Files.Select(f => f.Name)) is { } name
            ? result.Files.First(f => f.Name == name)
            : null;
        if (file is null)
            return ChartContents.None;

        try
        {
            return SongFile.ReadFromSng(sng, header, file, ChartReader.MaxBytes, cancellationToken) is { } bytes
                ? ChartContents.Read(bytes, file.Name, ChartReadOptions.From(pairs))
                : null;
        }
        catch (IOException)
        {
            // A stored chart that runs past the end of the file.
            return null;
        }
    }

    private static ChartContents? ReadFolderChart(
        string folder,
        IReadOnlyList<FolderFileEntry> files,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken cancellationToken)
    {
        if (ChartReader.Pick(files.Select(f => f.RelativePath).Where(n => !n.Contains('/'))) is not { } name)
            return ChartContents.None;

        // The listing's size saves asking the file system again.
        if (files.First(f => f.RelativePath == name).Length > ChartReader.MaxBytes)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(Path.Combine(folder, name));
        return ChartContents.Read(bytes, name, ChartReadOptions.From(pairs));
    }

    private static bool HasLength(IReadOnlyList<KeyValuePair<string, string>> pairs) => SongHeadline.From(pairs).LengthMs is not null;
}
