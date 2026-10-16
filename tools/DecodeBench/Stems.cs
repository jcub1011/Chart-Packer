using ChartShared.Audio.Decoding;
using ChartShared.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace DecodeBench;

/// <summary>Where one stem is: a loose audio file, or a file stored in a <c>.sng</c> that's read in place.</summary>
/// <param name="Label">What the results call it: never its path, so a library's layout isn't written down.</param>
/// <param name="Entry">The stored name inside the <c>.sng</c> at <paramref name="Path"/>, or <see langword="null"/> for a loose file.</param>
/// <param name="Seconds">How long it is, for the real-time factor.</param>
internal sealed record StemLocation(string Label, string Path, string? Entry, double Seconds)
{
    /// <summary>
    /// The environment variable the stems are handed to BenchmarkDotNet's benchmark processes in, which only see
    /// the labels: the host finds the stems, and each process opens the one it's given.
    /// </summary>
    public const string Variable = "DECODEBENCH_STEMS";

    private const char FieldSeparator = '\u001F';
    private const char RecordSeparator = '\u001E';

    private const int BufferSize = 64 * 1024;

    private static readonly string[] AudioExtensions = [".ogg", ".opus", ".mp3", ".wav"];

    public string Name => Entry ?? System.IO.Path.GetFileName(Path);

    public string Format => System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();

    public static string Serialize(IEnumerable<StemLocation> stems) =>
        string.Join(RecordSeparator, stems.Select(s => string.Join(FieldSeparator, s.Label, s.Path, s.Entry ?? "", s.Seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>The stems the host found, from <see cref="Variable"/>.</summary>
    public static IReadOnlyList<StemLocation> FromEnvironment()
    {
        string value = Environment.GetEnvironmentVariable(Variable) ?? throw new InvalidOperationException($"{Variable} isn't set: run DecodeBench itself, not its benchmarks.");
        return [.. value.Split(RecordSeparator).Select(record =>
        {
            string[] fields = record.Split(FieldSeparator);
            return new StemLocation(fields[0], fields[1], fields[2].Length == 0 ? null : fields[2], double.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture));
        })];
    }

    /// <summary>
    /// The stems at <paramref name="path"/>: an audio file, a <c>.sng</c>'s audio, or a directory's, searched
    /// recursively. A directory keeps only its <paramref name="perFormat"/> longest stems of each format.
    /// Each is opened once, and one the app can't decode is left out.
    /// </summary>
    public static List<StemLocation> Find(string path, int perFormat)
    {
        if (Directory.Exists(path))
        {
            return [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .SelectMany(FromFile)
                .GroupBy(stem => stem.Format)
                .SelectMany(group => group.OrderByDescending(stem => stem.Seconds).Take(perFormat))];
        }

        if (File.Exists(path))
            return [.. FromFile(path)];

        throw new FileNotFoundException($"'{path}' doesn't exist.");
    }

    /// <summary>Opens the stem, for as long as the result isn't disposed.</summary>
    public OpenStem Open()
    {
        if (Entry is null)
            return new OpenStem(Name, null, () => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize));

        var read = SngReader.Read(Path);
        var file = read.Files.FirstOrDefault(f => f.Name == Entry) ?? throw new FileNotFoundException($"'{Entry}' isn't in the .sng.");
        byte[] mask = read.Header!.Mask.ToArray();
        var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new OpenStem(Name, handle, () => new BufferedStream(new SngEntryStream(handle, file, mask), BufferSize));
    }

    private static IEnumerable<StemLocation> FromFile(string path)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (AudioExtensions.Contains(extension))
            return Probe(path, null, "loose");

        if (extension != ".sng")
            return [];

        var read = SngReader.Read(path);
        if (read.Header is null)
            return [];

        return [.. read.Files
            .Where(f => AudioExtensions.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant()))
            .SelectMany(f => Probe(path, f.Name, "sng"))];
    }

    /// <summary>The stem with a label of its shape, or nothing when the app can't decode it.</summary>
    private static IEnumerable<StemLocation> Probe(string path, string? entry, string kind)
    {
        var unlabelled = new StemLocation("", path, entry, 0);
        try
        {
            using var stem = unlabelled.Open();
            using var source = stem.OpenSource();
            double seconds = source.LengthFrames >= 0 ? (double)source.LengthFrames / source.SampleRate : 0;
            var length = TimeSpan.FromSeconds(seconds);
            string label = $"{unlabelled.Format} {source.Channels}ch {(int)length.TotalMinutes}m{length.Seconds:00}s {unlabelled.Name} ({kind})";
            return [unlabelled with { Label = label, Seconds = seconds }];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Skipped {unlabelled.Name} ({kind}): {ex.Message}");
            return [];
        }
    }
}

/// <summary>A stem that's open: streams and decoders over it can be made on any thread.</summary>
internal sealed class OpenStem(string name, SafeFileHandle? handle, Func<Stream> open) : IDisposable
{
    /// <summary>The stored name, for its extension.</summary>
    public string Name { get; } = name;

    /// <summary>A new read-only stream over the file, as the app opens it.</summary>
    public Stream OpenStream() => open();

    /// <summary>A new decoder over the file, on <paramref name="backend"/>.</summary>
    public IPcmSource OpenSource(AudioBackend backend = AudioBackend.Managed) => AudioDecoder.Open(OpenStream(), Name, backend);

    public void Dispose() => handle?.Dispose();
}
