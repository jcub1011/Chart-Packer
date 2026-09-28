using ChartPacker.Audio.Decoding;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartPacker.Audio.Playback;

/// <summary>A song's track, opened for playback.</summary>
/// <param name="Source">The decoder, or <see langword="null"/> when the track couldn't be opened.</param>
/// <param name="Error">Why the track couldn't be opened, if it couldn't.</param>
public sealed record OpenedTrack(SongTrack Track, IPcmSource? Source, string? Error);

/// <summary>
/// A song's audio tracks, open for decoding: stored stems are read straight out of the
/// <c>.sng</c> or the folder, and tracks a draft adds from the files they'll be copied from.
/// </summary>
/// <remarks>
/// Every file is opened for reading only, sharing reads, so nothing can replace or delete it
/// until this is disposed: dispose it before saving the song. All of a <c>.sng</c>'s tracks read
/// through one handle.
/// </remarks>
public sealed class SongAudio : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly SafeFileHandle? _sngHandle;
    private readonly List<OpenedTrack> _tracks;

    private SongAudio(SafeFileHandle? sngHandle, List<OpenedTrack> tracks)
    {
        _sngHandle = sngHandle;
        _tracks = tracks;
    }

    public IReadOnlyList<OpenedTrack> Tracks => _tracks;

    /// <summary>The tracks that could be opened, by stored name, for a <see cref="Mixing.StemMixer"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, IPcmSource>> Sources =>
        _tracks.Where(t => t.Source is not null).Select(t => KeyValuePair.Create(t.Track.StoredName, t.Source!)).ToList();

    /// <summary>Opens <paramref name="tracks"/> of <paramref name="entry"/>. A track that can't be opened is kept, with the reason.</summary>
    /// <exception cref="IOException">The <c>.sng</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The <c>.sng</c> can't be read.</exception>
    public static SongAudio Open(SongEntry entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(tracks);

        SafeFileHandle? handle = null;
        Dictionary<string, SngFileEntry>? stored = null;
        ReadOnlyMemory<byte> mask = default;
        var opened = new List<OpenedTrack>(tracks.Count);
        try
        {
            if (entry.Kind == SongEntryKind.SngFile && tracks.Any(t => !t.IsStaged))
            {
                // The handle is opened first, so the index read is of the file it reads.
                handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var read = SngReader.Read(entry.FullPath, cancellationToken);
                if (read.Header is { } header)
                {
                    mask = header.Mask;
                    stored = new Dictionary<string, SngFileEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in read.Files)
                        stored.TryAdd(file.Name, file);
                }
            }

            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                opened.Add(OpenTrack(entry, track, handle, stored, mask.Span));
            }
        }
        catch
        {
            foreach (var track in opened)
                track.Source?.Dispose();
            handle?.Dispose();
            throw;
        }

        return new SongAudio(handle, opened);
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
            track.Source?.Dispose();
        _sngHandle?.Dispose();
    }

    private static OpenedTrack OpenTrack(SongEntry entry, SongTrack track, SafeFileHandle? handle, Dictionary<string, SngFileEntry>? stored, ReadOnlySpan<byte> mask)
    {
        try
        {
            Stream stream;
            if (track.StagedSourcePath is { } source)
            {
                stream = OpenFile(source);
            }
            else if (entry.Kind == SongEntryKind.SngFile)
            {
                if (handle is null || stored is null || !stored.TryGetValue(track.StoredName, out var file))
                    return new OpenedTrack(track, null, "The file isn't in the .sng any more.");
                stream = new BufferedStream(new SngEntryStream(handle, file, mask), BufferSize);
            }
            else
            {
                stream = OpenFile(Path.Combine(entry.FullPath, track.StoredName));
            }

            return new OpenedTrack(track, AudioDecoder.Open(stream, track.StoredName), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new OpenedTrack(track, null, ex.Message);
        }
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
}
