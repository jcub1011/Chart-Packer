using System.Globalization;
using ATL;

namespace ChartPacker.Core.Scanning;

/// <summary>What an audio file's header and tags say about it.</summary>
/// <param name="Title">The title tag, or <see langword="null"/> when it has none.</param>
/// <param name="Artist">The artist tag, or <see langword="null"/>.</param>
/// <param name="Album">The album tag, or <see langword="null"/>.</param>
/// <param name="Genre">The genre tag, or <see langword="null"/>.</param>
/// <param name="Year">The recording year, or <see langword="null"/>.</param>
/// <param name="DurationMs">The duration in whole milliseconds, or <see langword="null"/> when it isn't known.</param>
/// <param name="Format">The format's short name, e.g. <c>OGG</c>, or <see langword="null"/>.</param>
/// <param name="SampleRate">The sample rate in Hz, or <see langword="null"/>.</param>
/// <remarks>
/// Read by <see href="https://github.com/Zeugma440/atldotnet">ATL.NET</see> (MIT License; see
/// <c>THIRD-PARTY-NOTICES.md</c>), as <see cref="AudioLength"/> does, from the file's headers and
/// tags without decoding the audio.
/// </remarks>
public sealed record AudioFileInfo(
    string? Title,
    string? Artist,
    string? Album,
    string? Genre,
    string? Year,
    long? DurationMs,
    string? Format,
    int? SampleRate)
{
    private const int BufferSize = 64 * 1024;

    static AudioFileInfo()
    {
        // ATL writes the stack trace of every file it can't parse to the console by default.
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    /// <summary>Reads the audio file at <paramref name="path"/>.</summary>
    /// <returns>What was read, or <see langword="null"/> when ATL can't read the file.</returns>
    /// <exception cref="IOException">The file can't be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static AudioFileInfo? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        Track track;
        try
        {
            track = new Track(stream, Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ATL reports damaged or unrecognised audio as assorted exceptions.
            return null;
        }

        double ms = track.DurationMs;
        int? year = track.Year is int y && y > 0 ? y : null;
        int rate = (int)track.SampleRate;
        return new AudioFileInfo(
            Text(track.Title),
            Text(track.Artist),
            Text(track.Album),
            Text(track.Genre),
            year?.ToString(CultureInfo.InvariantCulture),
            ms >= 1 && ms < long.MaxValue ? (long)ms : null,
            Text(track.AudioFormat?.ShortName),
            rate > 0 ? rate : null);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
