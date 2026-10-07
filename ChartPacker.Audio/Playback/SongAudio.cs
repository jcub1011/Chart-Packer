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
/// <c>.sng</c> or the folder, and tracks a draft adds from the files they'll be copied from. A
/// new chart, which has no song yet, has only the latter.
/// </summary>
/// <remarks>
/// Every file is opened for reading only, sharing reads, so nothing can replace or delete it
/// until this is disposed: dispose it before saving the song. All of a <c>.sng</c>'s tracks read
/// through one handle. Each source is read by one thread at a time, but separate sources, including
/// those of <see cref="OpenSource"/>, may be read on separate threads.
/// </remarks>
public sealed class SongAudio : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly SongEntry? _entry;
    private readonly SafeFileHandle? _sngHandle;
    private readonly Dictionary<string, SngFileEntry>? _stored;
    private readonly ReadOnlyMemory<byte> _mask;
    private readonly List<OpenedTrack> _tracks;

    private SongAudio(SongEntry? entry, SafeFileHandle? sngHandle, Dictionary<string, SngFileEntry>? stored, ReadOnlyMemory<byte> mask, List<OpenedTrack> tracks)
    {
        _entry = entry;
        _sngHandle = sngHandle;
        _stored = stored;
        _mask = mask;
        _tracks = tracks;
    }

    public IReadOnlyList<OpenedTrack> Tracks => _tracks;

    /// <summary>The tracks that could be opened, by stored name, for a <see cref="Mixing.StemMixer"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, IPcmSource>> Sources =>
        [.. _tracks.Where(t => t.Source is not null).Select(t => KeyValuePair.Create(t.Track.StoredName, t.Source!))];

    /// <summary>Opens <paramref name="tracks"/> of <paramref name="entry"/>. A track that can't be opened is kept, with the reason.</summary>
    /// <param name="entry">The song, or null for a new chart's, whose tracks are all staged.</param>
    /// <exception cref="ArgumentException">There's no song, and a track isn't staged.</exception>
    /// <exception cref="IOException">The <c>.sng</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The <c>.sng</c> can't be read.</exception>
    public static SongAudio Open(SongEntry? entry, IReadOnlyList<SongTrack> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (entry is null && tracks.Any(t => !t.IsStaged))
            throw new ArgumentException("Only staged tracks can be opened without a song.", nameof(tracks));

        SafeFileHandle? handle = null;
        Dictionary<string, SngFileEntry>? stored = null;
        ReadOnlyMemory<byte> mask = default;
        var opened = new List<OpenedTrack>(tracks.Count);
        try
        {
            if (entry?.Kind == SongEntryKind.SngFile && tracks.Any(t => !t.IsStaged))
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

        return new SongAudio(entry, handle, stored, mask, opened);
    }

    /// <summary>
    /// Opens another decoder for <paramref name="track"/>, apart from the one in <see cref="Tracks"/>, so that the
    /// two can be read on separate threads. The caller disposes it, before this.
    /// </summary>
    /// <exception cref="InvalidDataException">The track isn't audio this can decode.</exception>
    /// <exception cref="IOException">The track's file can't be read, or isn't in the <c>.sng</c>.</exception>
    /// <exception cref="UnauthorizedAccessException">The track's file can't be read.</exception>
    /// <exception cref="ObjectDisposedException">This has been disposed.</exception>
    public IPcmSource OpenSource(SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return OpenDecoder(_entry, track, _sngHandle, _stored, _mask.Span);
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
            track.Source?.Dispose();
        _sngHandle?.Dispose();
    }

    private static OpenedTrack OpenTrack(SongEntry? entry, SongTrack track, SafeFileHandle? handle, Dictionary<string, SngFileEntry>? stored, ReadOnlySpan<byte> mask)
    {
        try
        {
            return new OpenedTrack(track, OpenDecoder(entry, track, handle, stored, mask), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new OpenedTrack(track, null, ex.Message);
        }
    }

    private static IPcmSource OpenDecoder(SongEntry? entry, SongTrack track, SafeFileHandle? handle, Dictionary<string, SngFileEntry>? stored, ReadOnlySpan<byte> mask)
    {
        Stream stream;
        if (track.StagedSourcePath is { } source)
        {
            stream = OpenFile(source);
        }
        else if (entry is null)
        {
            throw new FileNotFoundException("The track isn't staged, and there's no song to read it from.");
        }
        else if (entry.Kind == SongEntryKind.SngFile)
        {
            if (handle is null || stored is null || !stored.TryGetValue(track.StoredName, out var file))
                throw new FileNotFoundException("The file isn't in the .sng any more.");
            ObjectDisposedException.ThrowIf(handle.IsClosed, handle);
            stream = new BufferedStream(new SngEntryStream(handle, file, mask), BufferSize);
        }
        else
        {
            stream = OpenFile(Path.Combine(entry.FullPath, track.StoredName));
        }

        return AudioDecoder.Open(stream, track.StoredName);
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
}
