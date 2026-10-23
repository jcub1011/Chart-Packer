using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>A pattern pack: the counts of a count-based model plus its manifest (spec 5).
/// Data only: nothing in it is executed.</summary>
public sealed class PatternPack
{
    public const int CurrentFormatVersion = 1;
    public const long MaxBytes = 64L * 1024L * 1024L;

    private readonly ConcurrentDictionary<ulong, IReadOnlyList<BeatPattern>> _candidateCache = new();
    private PatternScorer? _scorer;

    public PatternPackManifest Manifest { get; }
    public IReadOnlyList<BeatPattern> Vocabulary { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<ulong, int>> Counts { get; }

    internal PatternScorer Scorer => _scorer ??= new PatternScorer(this);

    internal PatternPack(PatternPackManifest manifest, IReadOnlyList<BeatPattern> vocabulary, IReadOnlyDictionary<string, IReadOnlyDictionary<ulong, int>> counts)
    {
        Manifest = manifest;
        Vocabulary = vocabulary;
        Counts = counts;
    }

    /// <summary>Every candidate pattern for a beat: subsets of its source the vocabulary knows, plus empty.</summary>
    public IReadOnlyList<BeatPattern> CandidatesFor(BeatPattern source) =>
        _candidateCache.GetOrAdd(source.Mask, mask => BuildCandidates(new BeatPattern(mask)));

    private List<BeatPattern> BuildCandidates(BeatPattern source)
    {
        var seen = new HashSet<ulong>();
        var candidates = new List<BeatPattern>();
        foreach (var known in Vocabulary)
        {
            if ((known.IsEmpty || known.IsSubsetOf(source)) && seen.Add(known.Mask))
                candidates.Add(known);
        }

        if (seen.Add(BeatPattern.Empty.Mask))
            candidates.Add(BeatPattern.Empty);
        if (!source.IsEmpty && seen.Add(source.Mask))
            candidates.Add(source);
        candidates.Sort(static (a, b) => a.Mask.CompareTo(b.Mask));
        return candidates;
    }
}

public sealed record PatternStyle(string Id, string Description);

public sealed record PatternPackManifest(
    int FormatVersion,
    int BeatSchemaVersion,
    string Name,
    string Author,
    string Description,
    IReadOnlyList<PatternStyle> Styles,
    bool SupportsAnyStyle,
    IReadOnlyList<string> Families,
    IReadOnlyList<string> Pairs,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> BackoffOrder,
    IReadOnlyDictionary<string, long> MinGaps,
    string License,
    string Checksum);

public sealed class PackValidationException(string message) : IOException(message);

/// <summary>Reads and validates a pattern pack file (spec 5.3). Never returns a half-used pack:
/// any problem throws <see cref="PackValidationException"/>.</summary>
public static class PatternPackLoader
{
    public static PatternPack Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        long length = new FileInfo(path).Length;
        if (length > PatternPack.MaxBytes)
            throw new PackValidationException($"Pack is {length} bytes, over the {PatternPack.MaxBytes} byte limit.");
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            throw new PackValidationException($"Pack can't be read: {e.Message}");
        }

        return LoadJson(json, Path.GetFileName(path));
    }

    public static PatternPack LoadJson(string json, string displayName = "pack")
    {
        PatternPackFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, PatternPackJsonContext.Default.PatternPackFile);
        }
        catch (Exception e)
        {
            throw new PackValidationException($"Pack {displayName} isn't valid JSON: {e.Message}");
        }

        if (file is null)
            throw new PackValidationException($"Pack {displayName} is empty.");
        if (file.FormatVersion != PatternPack.CurrentFormatVersion)
            throw new PackValidationException($"Pack {displayName} is format {file.FormatVersion}, this app reads {PatternPack.CurrentFormatVersion}.");
        if (file.BeatSchemaVersion != BeatPattern.SchemaVersion)
            throw new PackValidationException($"Pack {displayName} uses beat schema {file.BeatSchemaVersion}, this app uses {BeatPattern.SchemaVersion}: retrain it.");
        if (file.Vocabulary is not { Count: > 0 })
            throw new PackValidationException($"Pack {displayName} has no vocabulary.");
        if (file.Vocabulary.Count > 256)
            throw new PackValidationException($"Pack {displayName} has {file.Vocabulary.Count} patterns, over the 256 limit.");

        var vocabulary = new List<BeatPattern>(file.Vocabulary.Count);
        foreach (string entry in file.Vocabulary)
        {
            if (!BeatPattern.TryParse(entry, out var pattern))
                throw new PackValidationException($"Pack {displayName} has an unparsable pattern '{entry}'.");
            vocabulary.Add(pattern);
        }

        if (!vocabulary.Contains(BeatPattern.Empty))
            throw new PackValidationException($"Pack {displayName} vocabulary lacks '-' (empty).");

        var counts = new Dictionary<string, IReadOnlyDictionary<ulong, int>>(StringComparer.Ordinal);
        if (file.Counts is not null)
        {
            foreach (var (context, row) in file.Counts)
            {
                if (string.IsNullOrWhiteSpace(context))
                    throw new PackValidationException($"Pack {displayName} has an empty context key.");
                var decoded = new Dictionary<ulong, int>();
                foreach (var (patternText, count) in row)
                {
                    if (count < 0 || count > int.MaxValue)
                        throw new PackValidationException($"Pack {displayName} has a non-finite count for '{patternText}'.");
                    if (!BeatPattern.TryParse(patternText, out var pattern))
                        throw new PackValidationException($"Pack {displayName} counts an unparsable pattern '{patternText}'.");
                    if (count == 0)
                        continue;
                    decoded[pattern.Mask] = count;
                }

                counts[context] = decoded;
            }
        }

        var manifest = new PatternPackManifest(
            file.FormatVersion,
            file.BeatSchemaVersion,
            file.Name ?? displayName,
            file.Author ?? "",
            file.Description ?? "",
            [.. (file.Styles ?? []).Select(s => new PatternStyle(s.Id ?? "", s.Description ?? ""))],
            file.SupportsAnyStyle,
            file.Families ?? [],
            file.Pairs ?? [],
            file.Features ?? DefaultFeatures,
            file.BackoffOrder ?? DefaultBackoffOrder,
            file.MinGaps ?? new Dictionary<string, long>(),
            file.License ?? "",
            file.Checksum ?? "");

        string actual = ChecksumOf(vocabulary, counts);
        if (!string.IsNullOrEmpty(manifest.Checksum) && !string.Equals(manifest.Checksum, actual, StringComparison.OrdinalIgnoreCase))
            throw new PackValidationException($"Pack {displayName} checksum mismatch: file may be damaged.");

        return new PatternPack(manifest, vocabulary, counts);
    }

    public static readonly IReadOnlyList<string> DefaultFeatures =
        ["Source", "Target", "SourceDifficulty", "Shape", "Instrument", "Prev1", "Prev2", "Prev3", "NextSource", "MeasurePlace", "Style"];

    /// <summary>Least important first: dropped first when backing off.</summary>
    public static readonly IReadOnlyList<string> DefaultBackoffOrder =
        ["Style", "MeasurePlace", "NextSource", "Prev3", "Prev2", "Prev1", "Shape", "SourceDifficulty"];

    public static string ChecksumOf(IReadOnlyList<BeatPattern> vocabulary, IReadOnlyDictionary<string, IReadOnlyDictionary<ulong, int>> counts)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder(256);
        foreach (var pattern in vocabulary.OrderBy(static p => p.Mask))
            sb.Append(pattern.Mask.ToString("x12")).Append(';');
        sb.Append('#');
        hasher.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));

        var keys = new string[counts.Count];
        int idx = 0;
        foreach (string k in counts.Keys)
            keys[idx++] = k;
        Array.Sort(keys, StringComparer.Ordinal);

        var cellBuf = new List<KeyValuePair<ulong, int>>(16);
        foreach (string context in keys)
        {
            sb.Clear();
            sb.Append(context).Append(':');
            var row = counts[context];
            cellBuf.Clear();
            foreach (var kv in row)
                cellBuf.Add(kv);
            cellBuf.Sort(static (a, b) => a.Key.CompareTo(b.Key));
            foreach (var (mask, count) in cellBuf)
                sb.Append(mask.ToString("x12")).Append('=').Append(count).Append(',');
            sb.Append(';');
            hasher.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // File DTO: kept separate from the validated model so hostile files fail before use.
    internal sealed class PatternPackFile
    {
        public int FormatVersion { get; set; }
        public int BeatSchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public List<PatternStyleFile>? Styles { get; set; }
        public bool SupportsAnyStyle { get; set; } = true;
        public List<string>? Families { get; set; }
        public List<string>? Pairs { get; set; }
        public List<string>? Features { get; set; }
        public List<string>? BackoffOrder { get; set; }
        public Dictionary<string, long>? MinGaps { get; set; }
        public List<string>? Vocabulary { get; set; }
        public Dictionary<string, Dictionary<string, int>>? Counts { get; set; }
        public string? License { get; set; }
        public string? Checksum { get; set; }
    }

    internal sealed class PatternStyleFile
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
    }
}

/// <summary>Writes a <see cref="PatternPack"/> to its single-file JSON form (spec 5.1).</summary>
public static class PatternPackWriter
{
    public static string ToJson(PatternPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var file = new PatternPackLoader.PatternPackFile
        {
            FormatVersion = pack.Manifest.FormatVersion,
            BeatSchemaVersion = pack.Manifest.BeatSchemaVersion,
            Name = pack.Manifest.Name,
            Author = pack.Manifest.Author,
            Description = pack.Manifest.Description,
            Styles = [.. pack.Manifest.Styles.Select(s => new PatternPackLoader.PatternStyleFile { Id = s.Id, Description = s.Description })],
            SupportsAnyStyle = pack.Manifest.SupportsAnyStyle,
            Families = [.. pack.Manifest.Families],
            Pairs = [.. pack.Manifest.Pairs],
            Features = [.. pack.Manifest.Features],
            BackoffOrder = [.. pack.Manifest.BackoffOrder],
            MinGaps = new Dictionary<string, long>(pack.Manifest.MinGaps),
            Vocabulary = [.. pack.Vocabulary.Select(static p => p.ToDisplay())],
            Counts = pack.Counts.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(e => new BeatPattern(e.Key).ToDisplay(), e => e.Value)),
            License = pack.Manifest.License,
            Checksum = pack.Manifest.Checksum,
        };
        return JsonSerializer.Serialize(file, PatternPackJsonContext.Default.PatternPackFile);
    }

    public static void Write(string path, PatternPack pack)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pack);
        string json = ToJson(pack);
        if (Encoding.UTF8.GetByteCount(json) > PatternPack.MaxBytes)
            throw new IOException($"Pack JSON is over the {PatternPack.MaxBytes} byte limit.");
        File.WriteAllText(path, json);
    }

    /// <summary>A small built-in demo pack for trying Patterns with no pack installed.
    /// Captures the study's headline figures (downbeats, rests, off-beat substitution); not a trained model.</summary>
    public static PatternPack Demo()
    {
        var vocab = new List<BeatPattern>
        {
            BeatPattern.Parse("-"), BeatPattern.Parse("1"), BeatPattern.Parse("&"),
            BeatPattern.Parse("1 &"), BeatPattern.Parse("e"),
        };
        Dictionary<ulong, int> Row(params (string Pattern, int Count)[] cells) =>
            cells.ToDictionary(c => BeatPattern.Parse(c.Pattern).Mask, c => c.Count);
        var counts = new Dictionary<string, IReadOnlyDictionary<ulong, int>>
        {
            ["global"] = Row(("1", 50), ("-", 30), ("&", 10), ("1 &", 10)),
            ["src=1"] = Row(("1", 60), (("-", 20)), (("&", 10)), (("1 &", 10))),
            ["src=1 &"] = Row(("1", 57), (("-", 28)), (("&", 11))),
            ["src=&"] = Row(("&", 50), (("-", 30)), (("1", 20))),
            ["src=-"] = Row((("-", 80)), (("1", 20))),
            // Target run-off: Hard keeps more, Easy rests more (study §5.1 keep ratios).
            ["src=1|tgt=Hard"] = Row(("1", 45), (("1 &", 30)), (("-", 5)), (("&", 15)), (("e", 5))),
            ["src=1|tgt=Medium"] = Row(("1", 55), (("-", 20)), (("1 &", 12)), (("&", 10)), (("e", 3))),
            ["src=1|tgt=Easy"] = Row(("1", 50), (("-", 35)), (("&", 10)), (("1 &", 3)), (("e", 2))),
            ["src=1 &|tgt=Hard"] = Row(("1 &", 45), (("1", 35)), (("-", 10)), (("&", 8)), (("e", 2))),
            ["src=1 &|tgt=Medium"] = Row(("1", 50), (("1 &", 20)), (("-", 18)), (("&", 10)), (("e", 2))),
            ["src=1 &|tgt=Easy"] = Row(("1", 45), (("-", 35)), (("&", 15)), (("1 &", 3)), (("e", 2))),
            ["src=&|tgt=Easy"] = Row((("&", 40)), (("-", 40)), (("1", 20))),
        };
        var manifest = new PatternPackManifest(
            PatternPack.CurrentFormatVersion,
            BeatPattern.SchemaVersion,
            "Demo (built-in)",
            "Chart Packer",
            "Small built-in demo for trying Patterns with no pack installed. Not a trained model.",
            [new PatternStyle("Any", "Blend of everything the demo knows.")],
            true,
            ["FiveFret"],
            ["Expert->Hard", "Expert->Medium", "Expert->Easy"],
            [.. PatternPackLoader.DefaultFeatures],
            [.. PatternPackLoader.DefaultBackoffOrder],
            new Dictionary<string, long>(),
            "",
            PatternPackLoader.ChecksumOf(vocab, counts));
        return new PatternPack(manifest, vocab, counts);
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PatternPackLoader.PatternPackFile))]
internal sealed partial class PatternPackJsonContext : JsonSerializerContext;
