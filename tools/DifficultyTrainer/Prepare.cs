using System.Text.Json;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>
/// Prepare (spec §6.1): scans a folder of songs and writes a training set — each usable part's
/// beats, patterns and features with the song's style label. Derived figures only (§6.3).
/// </summary>
internal static class Prepare
{
    /// <summary>Source→target pairs trained (Expert→each plus each-to-below, §3.6).</summary>
    private static readonly (ChartDifficulty From, ChartDifficulty To)[] Pairs =
    [
        (ChartDifficulty.Expert, ChartDifficulty.Hard),
        (ChartDifficulty.Expert, ChartDifficulty.Medium),
        (ChartDifficulty.Expert, ChartDifficulty.Easy),
        (ChartDifficulty.Hard, ChartDifficulty.Medium),
        (ChartDifficulty.Medium, ChartDifficulty.Easy),
    ];

    private static readonly ChartInstrument[] FiveFretParts =
    [
        ChartInstrument.Guitar,
        ChartInstrument.Bass,
        ChartInstrument.Keys,
        ChartInstrument.Rhythm,
    ];

    public static int Run(string[] args)
    {
        string songs = Arg(args, "--songs", "");
        string output = Arg(args, "--out", "training-set.json");
        string styleBy = Arg(args, "--style-by", "studio");
        string styles = Arg(args, "--styles", "");
        double heldOut = double.TryParse(Arg(args, "--held-out", "0.2"), out double h) ? h : 0.2;
        bool excludeGenerated = Arg(args, "--exclude-generated", "off") == "on";
        if (string.IsNullOrWhiteSpace(songs) || !Directory.Exists(songs))
        {
            Console.Error.WriteLine("Usage: prepare --songs <folder> --out <training-set.json> [--style-by studio|charter] [--styles map.txt] [--held-out 0.2] [--exclude-generated on]");
            return 2;
        }

        var styleMap = LoadStyleMap(styles);
        var examples = new List<PreparedPart>();
        var seen = new HashSet<string>();
        int songsSeen = 0;
        int partsUsed = 0;
        int partsSkipped = 0;

        var songDirs = Directory.EnumerateDirectories(songs, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, "notes.mid")))
            .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string dir in songDirs)
        {
            songsSeen++;
            string studio = new DirectoryInfo(Path.GetDirectoryName(dir)!).Name;
            var ini = ReadSongIni(Path.Combine(dir, "song.ini"));
            string artist = ini.GetValueOrDefault("artist", new DirectoryInfo(dir).Name);
            string title = ini.GetValueOrDefault("name", "");
            string songKey = SongKeyOf(artist, title);
            string charter = ini.GetValueOrDefault("charter", "Unknown");
            string style = styleBy == "charter"
                ? MapStyle(charter, styleMap) ?? charter
                : studio;

            // The song is read as the games read it, with its song.ini's settings, as the apps do (§3.3).
            var readOptions = ChartReadOptions.From([.. ini.Select(static kv => KeyValuePair.Create(kv.Key, kv.Value))]);
            EditableChart chart;
            try
            {
                byte[] midi = File.ReadAllBytes(Path.Combine(dir, "notes.mid"));
                chart = MidiChartLoader.Load(midi, readOptions).Chart;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Skipping {dir}: {ex.Message}");
                continue;
            }

            foreach (var part in FiveFretParts)
            {
                var whole = chart.PartOf(part);
                if (whole is null)
                    continue;
                IReadOnlyDictionary<ChartDifficulty, double>? ranks = null;
                foreach (var (from, to) in Pairs)
                {
                    if (whole[from].Notes.Count == 0 || whole[to].Notes.Count == 0)
                        continue;
                    string dedupe = DedupeKey(whole[from], whole[to]);
                    if (!seen.Add(part + "|" + from + "|" + to + "|" + dedupe))
                        continue;

                    PreparedPart? example;
                    try
                    {
                        var rules = RulesTicks(chart, part, from, to, readOptions);
                        if (excludeGenerated && rules is not null && LooksGenerated(rules, whole[to]))
                        {
                            partsSkipped++;
                            continue;
                        }

                        ranks ??= NeuralConditioning.SourceRanks(chart, part, readOptions);
                        example = BuildExample(chart, part, from, to, songKey, style, ranks.TryGetValue(from, out double rank) ? rank : null, rules ?? []);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Skipping {dir} {part} {from}->{to}: {ex.Message}");
                        continue;
                    }

                    if (example is not null && example.SourcePatterns.Length > 0)
                    {
                        examples.Add(example);
                        partsUsed++;
                    }
                }
            }

            if (songsSeen % 200 == 0)
                Console.WriteLine($"  ... {songsSeen} songs, {partsUsed} parts");
        }

        // Split by song so the same song is never on both sides (§4, §6.2), by a stable hash of its key rather than
        // alphabetically, so the held-out songs aren't all one end of the alphabet.
        var songKeys = examples.Select(static e => e.SongKey).Distinct(StringComparer.Ordinal).ToList();
        var heldOutKeys = songKeys.Where(k => StableHash(k) % 10_000 < heldOut * 10_000).ToHashSet(StringComparer.Ordinal);
        if (heldOutKeys.Count == 0 && songKeys.Count > 1)
            heldOutKeys.Add(songKeys.OrderBy(StableHash).First());

        var stylesUsed = examples.Select(static e => e.Style).Distinct().OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        var instrumentsUsed = FiveFretParts.Select(static p => p.ToString()).Where(p => examples.Any(e => e.Instrument == p)).ToArray();
        var set = new TrainingSet(
            BeatPipeline.BeatSchemaVersion,
            ["Any", .. stylesUsed],
            [.. Pairs.Select(static p => NeuralConditioning.PairName(p.From, p.To))],
            instrumentsUsed,
            examples,
            $"DifficultyTrainer {ThisVersion}");

        using (var stream = File.Create(output))
            JsonSerializer.Serialize(stream, set, TrainingSetJsonContext.Default.TrainingSet);

        Console.WriteLine($"Songs: {songsSeen}; usable parts: {partsUsed}; skipped as generated: {partsSkipped}.");
        Console.WriteLine($"Held-out songs: {heldOutKeys.Count} of {songKeys.Count} (split recorded in {output}.heldout).");
        File.WriteAllLines(output + ".heldout", heldOutKeys.Order(StringComparer.Ordinal));
        Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024 / 1024} MB).");
        // Every label must be one of its beat's candidates, or decoding could never
        // reproduce the reference. Count violations (must be zero).
        long badLabels = 0;
        long totalLabels = 0;
        foreach (var e in examples)
        {
            var candidates = e.Candidates();
            for (int i = 0; i < e.Labels.Length; i++)
            {
                totalLabels++;
                if (!candidates[i].Contains(e.Labels[i]) || !candidates[i].Contains(e.RulesLabels[i]))
                    badLabels++;
            }
        }

        Console.WriteLine($"Label check: {badLabels} of {totalLabels} labels outside their beat's candidates (must be 0).");
        Guidance(examples);
        return 0;
    }

    private static PreparedPart? BuildExample(
        EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to,
        string songKey, string style, double? sourceRank, long[] rulesTicks)
    {
        var beats = BeatPipeline.Extract(chart, part, from);
        if (beats.Count == 0)
            return null;

        long[] targetTicks = [.. chart.PartOf(part)![to].Notes.AsSpan().ToArray().Select(static n => n.Tick).Distinct().Order()];
        long tolerance = Math.Max(1, chart.Resolution / 8); // within a 32nd: shifted (§5.1).

        int n = beats.Count;
        var scalars = new sbyte[n * PreparedPart.ScalarsPerBeat];
        var riffIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var riffGroups = new int[n];
        for (int i = 0; i < n; i++)
        {
            PreparedParts.Pack(beats[i], scalars.AsSpan(i * PreparedPart.ScalarsPerBeat, PreparedPart.ScalarsPerBeat));
            if (!riffIds.TryGetValue(beats[i].RiffKey, out int id))
                riffIds[beats[i].RiffKey] = id = riffIds.Count;
            riffGroups[i] = id;
        }

        return new PreparedPart(
            songKey, style, sourceRank, part.ToString(),
            NeuralConditioning.PairName(from, to),
            [.. beats.Select(static b => b.SourcePatternId)],
            [.. beats.Select(static b => b.SourceOffsets48)],
            scalars,
            [.. beats.Select(static b => (float)b.SecondsPerBeat)],
            riffGroups,
            [.. beats.Select(static b => b.StarPowerPhrase)],
            [.. beats.Select(static b => b.SoloPhrase)],
            LabelsOf(beats, targetTicks, tolerance),
            LabelsOf(beats, rulesTicks, tolerance));
    }

    /// <summary>
    /// Each beat's label: the source onsets with a chord of <paramref name="kept"/> (sorted) within
    /// <paramref name="tolerance"/>, as the nearest of the beat's candidates, so every label is decodable.
    /// </summary>
    private static int[] LabelsOf(IReadOnlyList<BeatFrame> beats, long[] kept, long tolerance)
    {
        var labels = new int[beats.Count];
        for (int i = 0; i < beats.Count; i++)
        {
            var beat = beats[i];
            var keptSlots = new List<int>();
            for (int k = 0; k < beat.SourceTicks.Length; k++)
            {
                if (Near(kept, beat.SourceTicks[k], tolerance))
                    keptSlots.Add(beat.SourceOffsets48[k]);
            }

            labels[i] = keptSlots.Count == 0
                ? BeatPattern.EmptyId
                : BeatPattern.NearestCandidate(BeatPattern.CandidatesFor(beat.SourceOffsets48), keptSlots);
        }

        return labels;
    }

    // Whether sorted holds a tick within tolerance of tick.
    private static bool Near(long[] sorted, long tick, long tolerance)
    {
        int index = Array.BinarySearch(sorted, tick);
        if (index >= 0)
            return true;
        index = ~index;
        return (index < sorted.Length && sorted[index] - tick <= tolerance)
            || (index > 0 && tick - sorted[index - 1] <= tolerance);
    }

    /// <summary>The chords Rules' draft keeps, by the shipped rules for the target (the evaluation's baseline, §6.6).</summary>
    private static long[]? RulesTicks(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to, ChartReadOptions readOptions)
    {
        try
        {
            var kind = ChartInstruments.KindOf(part);
            var draft = Reducer.Reduce(chart, part, from, to, FretReductionRules.Default(kind, to), options: readOptions);
            return [.. draft.Notes.Select(static n => n.Tick).Distinct().Order()];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the target looks generated rather than hand-made (§6.2): its chords are Rules' draft's.</summary>
    private static bool LooksGenerated(long[] rulesTicks, EditableTrack actual) =>
        rulesTicks.SequenceEqual(actual.Notes.AsSpan().ToArray().Select(static n => n.Tick).Distinct().Order());

    private static string DedupeKey(EditableTrack a, EditableTrack b)
    {
        unchecked
        {
            int h = 17;
            foreach (var n in a.Notes.AsSpan().ToArray())
                h = h * 31 + n.Tick.GetHashCode();
            h = h * 31 + 0x9e37;
            foreach (var n in b.Notes.AsSpan().ToArray())
                h = h * 31 + n.Tick.GetHashCode();
            return h.ToString("x8");
        }
    }

    // FNV-1a over the key's characters: the same on every run and machine, unlike string.GetHashCode.
    private static uint StableHash(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in key)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private static string SongKeyOf(string artist, string title)
    {
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Norm(artist) + "|" + Norm(title);
    }

    private static Dictionary<string, string> ReadSongIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return result;
    }

    private static Dictionary<string, string> LoadStyleMap(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;
        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;
            var bits = line.Split(["=>", "="], 2, StringSplitOptions.TrimEntries);
            if (bits.Length == 2)
                result[bits[0]] = bits[1];
        }

        return result;
    }

    private static string? MapStyle(string charter, Dictionary<string, string> map)
    {
        if (map.TryGetValue(charter, out string? style))
            return style;
        return null;
    }

    private static void Guidance(List<PreparedPart> examples)
    {
        // Spec §6.2: warn when the data is thin for what's asked.
        var perPair = examples.GroupBy(static e => e.Pair).ToDictionary(static g => g.Key, static g => g.Count());
        foreach (var (pair, count) in perPair.OrderBy(static kv => kv.Key))
        {
            long beats = examples.Where(e => e.Pair == pair).Sum(e => (long)e.SourcePatterns.Length);
            Console.WriteLine($"  {pair}: {count} parts, {beats / 1000}k beats.");
            if (count < 1000)
                Console.WriteLine($"  WARNING: {pair} has {count} parts; the study's ~5,000 parts per target is the reference for enough (§6.2).");
        }
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return fallback;
    }

    private const string ThisVersion = "2.0";
}
