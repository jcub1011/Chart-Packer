using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Merging;

/// <summary>The format of a song's chart.</summary>
public enum MergeChartFormat
{
    /// <summary>The song has no chart, or it couldn't be read.</summary>
    None,

    /// <summary><c>notes.mid</c>.</summary>
    Midi,

    /// <summary><c>notes.chart</c>, converted to MIDI for merging.</summary>
    Chart,
}

/// <summary>Where a merged song's value for a metadata key comes from.</summary>
public enum MergeKeyRule
{
    /// <summary>The value the sources share, or the one chosen when they differ.</summary>
    Chosen,

    /// <summary>Every source's charters, combined by <see cref="CharterList"/>.</summary>
    Charter,

    /// <summary>The source of the part the key describes, e.g. <c>diff_drums</c> from the drums' source.</summary>
    Part,

    /// <summary>The base chart's source: settings for how the chart is read, e.g. <c>hopo_frequency</c>.</summary>
    Base,

    /// <summary>The base chart's delay, a <c>.chart</c>'s <c>Offset</c> included.</summary>
    Delay,

    /// <summary>The audio's source: the preview and the tuning.</summary>
    Audio,

    /// <summary>The album art's source.</summary>
    AlbumArt,

    /// <summary>The background image's source.</summary>
    Background,

    /// <summary>The background video's source.</summary>
    Video,

    /// <summary>Worked out from the merged song.</summary>
    SongLength,
}

/// <summary>A part one source has.</summary>
/// <param name="Part">The part.</param>
/// <param name="Difficulties">The difficulties it has notes for, as <see cref="ChartContents.ReadParts"/> reads them; empty for a part without difficulties, such as vocals.</param>
/// <param name="Difficulty">The source's song.ini difficulty for it (its first key), if it has one.</param>
/// <param name="NoteCounts">Its notes on each difficulty or harmony line, as <see cref="ChartPart.NoteCounts"/>.</param>
public sealed record MergeSourcePart(MidiPart Part, IReadOnlyList<ChartDifficulty> Difficulties, string? Difficulty, IReadOnlyList<NoteCount> NoteCounts);

/// <summary>A key whose value differs between the sources.</summary>
/// <param name="Key">The key.</param>
/// <param name="Values">Each source's value, in source order; <see langword="null"/> where a source doesn't have it.</param>
public sealed record MetadataConflict(string Key, IReadOnlyList<string?> Values);

/// <summary>One of the songs being merged, as read for merging.</summary>
public sealed class MergeSource
{
    private readonly Dictionary<string, string> _values;

    internal MergeSource(SongEntry entry, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<string> storedNames)
    {
        Entry = entry;
        Pairs = pairs;
        StoredNames = storedNames;
        _values = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Headline = SongHeadline.From(pairs);
        AudioFiles = [.. storedNames.Where(n => AudioStems.StemOf(n) is not null).OrderBy(n => AudioStems.OrderOf(AudioStems.StemOf(n)!)).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)];
        AlbumArt = Scanning.AlbumArt.Pick(storedNames, n => n);
        BackgroundImage = SongBackgrounds.Pick(storedNames, n => n, BackgroundKind.Image);
        Video = SongBackgrounds.Pick(storedNames, n => n, BackgroundKind.Video);
    }

    public SongEntry Entry { get; }

    /// <summary>Its metadata, with legacy keys under the keys that replaced them, each key once.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Pairs { get; }

    public SongHeadline Headline { get; }

    /// <summary>The files directly in the song.</summary>
    public IReadOnlyList<string> StoredNames { get; }

    public MergeChartFormat Format { get; internal set; }

    /// <summary>Its chart as MIDI; <see langword="null"/> when it has none that could be read.</summary>
    public MidiChart? Chart { get; internal set; }

    /// <summary>Why its chart couldn't be read, when it has one that couldn't.</summary>
    public string? ChartError { get; internal set; }

    /// <summary>A <c>.chart</c>'s tracks that didn't convert to MIDI exactly.</summary>
    public IReadOnlyList<ChartMergeWarning> ConversionWarnings { get; internal set; } = [];

    /// <summary>The parts its chart has, in catalog order.</summary>
    public IReadOnlyList<MergeSourcePart> Parts { get; internal set; } = [];

    /// <summary>Its audio files, the preview included, in stem order.</summary>
    public IReadOnlyList<string> AudioFiles { get; }

    /// <summary>Its album art's stored name.</summary>
    public string? AlbumArt { get; }

    /// <summary>Its background image's stored name.</summary>
    public string? BackgroundImage { get; }

    /// <summary>Its background video's stored name.</summary>
    public string? Video { get; }

    /// <summary>Its value for <paramref name="key"/>, ignoring case, trimmed; <see langword="null"/> when it has none.</summary>
    public string? Value(string key) => _values.TryGetValue(key, out string? value) ? value.Trim() : null;

    /// <summary>Whether its chart has <paramref name="partId"/>.</summary>
    public bool HasPart(string partId) => Parts.Any(p => p.Part.Id == partId);
}

/// <summary>The songs to merge, read, with the parts and metadata they have and where they differ.</summary>
public sealed class SongMergeAnalysis
{
    private static readonly HashSet<string> BaseKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "hopo_frequency", "hopofreq", "eighthnote_hopo", "sustain_cutoff_threshold", "multiplier_note", "end_events", "sysex_slider", "sysex_pro_slide",
    };

    private static readonly HashSet<string> AudioKeys = new(StringComparer.OrdinalIgnoreCase) { "preview_start_time", "preview_end_time", "preview", "tuning_offset_cents" };

    private static readonly HashSet<string> VideoKeys = new(StringComparer.OrdinalIgnoreCase) { "video", "video_loop", "video_start_time", "video_end_time" };

    private SongMergeAnalysis(IReadOnlyList<MergeSource> sources)
    {
        Sources = sources;
        Parts = sources
            .SelectMany(s => s.Parts.Select(p => p.Part))
            .DistinctBy(p => p.Id)
            .OrderBy(MidiPartCatalog.OrderOf)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var keys = sources.SelectMany(s => s.Pairs.Select(p => p.Key)).Distinct(StringComparer.OrdinalIgnoreCase);
        Conflicts = keys
            .Where(k => RuleOf(k) == MergeKeyRule.Chosen)
            .Select(k => new MetadataConflict(k, sources.Select(s => s.Value(k)).ToList()))
            .Where(c => c.Values.Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();
    }

    /// <summary>The songs, in the order they were given.</summary>
    public IReadOnlyList<MergeSource> Sources { get; }

    /// <summary>Every part any of them has, in catalog order.</summary>
    public IReadOnlyList<MidiPart> Parts { get; }

    /// <summary>The keys, other than those <see cref="RuleOf"/> works out, whose values differ.</summary>
    public IReadOnlyList<MetadataConflict> Conflicts { get; }

    /// <summary>The indexes of the sources that have <paramref name="partId"/>.</summary>
    public IReadOnlyList<int> SourcesWith(string partId) =>
        Enumerable.Range(0, Sources.Count).Where(i => Sources[i].HasPart(partId)).ToList();

    /// <summary>Where the merged song's value for <paramref name="key"/> comes from.</summary>
    public static MergeKeyRule RuleOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ToLowerInvariant() switch
        {
            "charter" => MergeKeyRule.Charter,
            "song_length" => MergeKeyRule.SongLength,
            "delay" => MergeKeyRule.Delay,
            "cover" => MergeKeyRule.AlbumArt,
            "background" => MergeKeyRule.Background,
            _ when MidiPartCatalog.OwnerOf(key) is not null => MergeKeyRule.Part,
            _ when BaseKeys.Contains(key) => MergeKeyRule.Base,
            _ when AudioKeys.Contains(key) => MergeKeyRule.Audio,
            _ when VideoKeys.Contains(key) => MergeKeyRule.Video,
            _ => MergeKeyRule.Chosen,
        };
    }

    /// <summary>Reads <paramref name="entries"/> for merging, on the thread pool.</summary>
    /// <remarks>A song whose chart can't be read is still read, with no parts, for its metadata and files.</remarks>
    /// <exception cref="IOException">A song's files can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public static Task<SongMergeAnalysis> LoadAsync(IReadOnlyList<SongEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(async () =>
        {
            var sources = new List<MergeSource>(entries.Count);
            foreach (var entry in entries)
                sources.Add(await LoadAsync(entry, cancellationToken).ConfigureAwait(false));
            return new SongMergeAnalysis(sources);
        }, cancellationToken);
    }

    private static async Task<MergeSource> LoadAsync(SongEntry entry, CancellationToken cancellationToken)
    {
        var stored = SongFile.ListTopLevel(entry, cancellationToken).Select(f => f.Name).ToList();
        var source = new MergeSource(entry, Normalize(await ReadPairsAsync(entry, stored, cancellationToken).ConfigureAwait(false)), stored);

        if (ChartReader.Pick(stored) is not { } chartName)
            return source;

        try
        {
            byte[]? bytes = await SongFile.ReadAsync(entry, chartName, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                source.ChartError = "The chart is too large to read.";
                return source;
            }

            var (chart, conversion) = MidiChart.Load(bytes, chartName, source.Pairs);
            source.Format = conversion is null ? MergeChartFormat.Midi : MergeChartFormat.Chart;
            source.Chart = chart;
            if (conversion is not null)
                source.ConversionWarnings = conversion.Warnings;

            source.Parts = PartsOf(source.Chart, source);
        }
        catch (ChartFormatException ex)
        {
            source.Format = MergeChartFormat.None;
            source.Chart = null;
            source.ChartError = ex.Message;
        }

        return source;
    }

    private static List<MergeSourcePart> PartsOf(MidiChart chart, MergeSource source)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(chart.Midi);
        var names = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.NameOf(chart.Midi, MidiFile.ReadEvents(chart.Midi, c))).OfType<string>().ToList();
        var contents = ChartContents.PartsOf(ChartReader.Read(chart.Midi, ChartFiles.MidiName, chart.Options), chart.Midi);

        return names
            .Select(MidiPartCatalog.PartOf)
            .OfType<MidiPart>()
            .DistinctBy(p => p.Id)
            .OrderBy(MidiPartCatalog.OrderOf)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p =>
            {
                var read = contents.GetValueOrDefault(p.Id);
                var difficulties = read?.Difficulties ?? LibraryDifficulties.None;
                return new MergeSourcePart(
                    p,
                    Enum.GetValues<ChartDifficulty>().Where(d => difficulties.HasFlag((LibraryDifficulties)(1 << (int)d))).ToList(),
                    p.Keys.Count > 0 ? source.Value(p.Keys[0]) : null,
                    read?.NoteCounts ?? []);
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadPairsAsync(SongEntry entry, IReadOnlyList<string> stored, CancellationToken cancellationToken)
    {
        if (entry.Kind == SongEntryKind.SngFile)
            return SngReader.Read(entry.FullPath, cancellationToken).Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();

        string? ini = stored.FirstOrDefault(n => n.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
        return ini is null ? [] : (await SongIni.ReadAsync(Path.Combine(entry.FullPath, ini), cancellationToken).ConfigureAwait(false)).Pairs;
    }

    /// <summary>
    /// <paramref name="pairs"/> with known keys in their canonical lower case, legacy keys under the
    /// keys that replaced them (which win when both are there), empty values dropped and each key once.
    /// </summary>
    public static List<KeyValuePair<string, string>> Normalize(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var result = new List<KeyValuePair<string, string>>(pairs.Count);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in pairs)
        {
            if (value.Trim().Length == 0)
                continue;

            bool legacy = KnownKeys.LegacyReplacements.TryGetValue(rawKey, out string? replacement);
            string key = legacy ? replacement! : KnownKeys.TryGet(rawKey, out var known) ? known.Key : rawKey;
            if (!index.TryGetValue(key, out int at))
            {
                index[key] = result.Count;
                result.Add(KeyValuePair.Create(key, value));
            }
            else if (!legacy)
            {
                result[at] = KeyValuePair.Create(key, value);
            }
        }

        return result;
    }
}
