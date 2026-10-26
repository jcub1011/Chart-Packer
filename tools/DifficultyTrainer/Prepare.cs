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
            Console.Error.WriteLine("Usage: prepare --songs <folder> --out <training-set.json> [--style-by studio|charter] [--styles map.txt] [--held-out 0.2]");
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

            EditableChart chart;
            try
            {
                byte[] midi = File.ReadAllBytes(Path.Combine(dir, "notes.mid"));
                chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;
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
                foreach (var (from, to) in Pairs)
                {
                    if (whole[from].Notes.Count == 0 || whole[to].Notes.Count == 0)
                        continue;
                    string dedupe = DedupeKey(whole[from], whole[to]);
                    if (!seen.Add(part + "|" + from + "|" + to + "|" + dedupe))
                        continue;
                    if (excludeGenerated && LooksGenerated(chart, part, from, to))
                    {
                        partsSkipped++;
                        continue;
                    }

                    var example = BuildExample(chart, part, from, to, songKey, style, TierOf(ini, part));
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

        // Split by song so the same song is never on both sides (§4, §6.2).
        var bySong = examples.GroupBy(static e => e.SongKey).OrderBy(static g => g.Key, StringComparer.Ordinal).ToList();
        int heldOutSongs = Math.Max(1, (int)(bySong.Count * heldOut));
        var heldOutKeys = bySong.Take(heldOutSongs).Select(static g => g.Key).ToHashSet(StringComparer.Ordinal);

        var stylesUsed = examples.Select(static e => e.Style).Distinct().OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        var set = new TrainingSet(
            BeatPipeline.BeatSchemaVersion,
            ["Any", .. stylesUsed],
            ["Easy", "Medium", "Hard"],
            Pairs.Select(static p => $"{p.From}->{p.To}").ToArray(),
            ["Guitar", "Bass", "Keys", "Rhythm"],
            examples,
            $"DifficultyTrainer {ThisVersion}");

        using (var stream = File.Create(output))
            JsonSerializer.Serialize(stream, set, TrainingSetJsonContext.Default.TrainingSet);

        Console.WriteLine($"Songs: {songsSeen}; usable parts: {partsUsed}; skipped as generated: {partsSkipped}.");
        Console.WriteLine($"Held-out songs: {heldOutSongs} of {bySong.Count} (split recorded in {output}.heldout).");
        File.WriteAllLines(output + ".heldout", heldOutKeys.Order(StringComparer.Ordinal));
        Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024 / 1024} MB).");
        // Every label must be one of its beat's candidates, or decoding could never
        // reproduce the reference. Count violations (must be zero).
        long badLabels = 0;
        long totalLabels = 0;
        foreach (var e in examples)
        {
            for (int i = 0; i < e.Labels.Length; i++)
            {
                totalLabels++;
                if (!e.Candidates[i].Contains(e.Labels[i]))
                    badLabels++;
            }
        }

        Console.WriteLine($"Label check: {badLabels} of {totalLabels} labels outside their beat's candidates (must be 0).");
        Guidance(examples);
        return 0;
    }

    private static PreparedPart? BuildExample(
        EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to,
        string songKey, string style, int? tier)
    {
        var whole = chart.PartOf(part)!;
        var sourceTrack = whole[from];
        var targetTrack = whole[to];

        var beats = BeatPipeline.Extract(chart, part, from);
        if (beats.Count == 0)
            return null;

        var targetByTick = targetTrack.Notes.GroupBy(static n => n.Tick).ToDictionary(static g => g.Key, static g => g.ToArray());
        var targetTicks = new HashSet<long>(targetByTick.Keys);
        var source = new List<int>();
        var candidates = new List<int[]>();
        var scalars = new List<sbyte>();
        var labels = new List<int>();

        int resolution = chart.Resolution;
        long tolerance = Math.Max(1, resolution / 8); // within a 32nd: shifted (§5.1).

        foreach (var beat in beats)
        {
            source.Add(beat.SourcePatternId);
            var beatCandidates = BeatPattern.CandidatesFor(beat.SourceOffsets48);
            candidates.Add([.. beatCandidates]);
            scalars.Add((sbyte)beat.GapBucket);

            // The label: the reference's kept pattern in this beat, snapped to source onsets.
            var keptSlots = new List<int>();
            foreach (long tick in beat.SourceTicks)
            {
                bool kept = targetTicks.Any(tt => Math.Abs(tt - tick) <= tolerance);
                if (kept)
                {
                    for (int k = 0; k < beat.SourceOffsets48.Length && k < beat.SourceTicks.Length; k++)
                    {
                        if (beat.SourceTicks[k] == tick)
                            keptSlots.Add(beat.SourceOffsets48[k]);
                    }
                }
            }

            // The label: the reference's kept pattern in this beat, snapped to source onsets
            // and mapped onto the beat's own candidates, so every label is decodable.
            int label = keptSlots.Count == 0
                ? BeatPattern.EmptyId
                : BeatPattern.NearestCandidate(beatCandidates, keptSlots);
            labels.Add(label);
        }

        var riffGroups = RiffGroupIds(beats);
        var scalarBytes = new sbyte[beats.Count * 8];
        for (int i = 0; i < beats.Count; i++)
        {
            var b = beats[i];
            scalarBytes[i * 8 + 0] = (sbyte)Math.Clamp(b.BeatInMeasure, -100, 100);
            scalarBytes[i * 8 + 1] = (sbyte)b.GapBucket;
            scalarBytes[i * 8 + 2] = (sbyte)(b.PhraseStart ? 1 : 0);
            scalarBytes[i * 8 + 3] = (sbyte)(b.SectionStart ? 1 : 0);
            scalarBytes[i * 8 + 4] = (sbyte)(b.HasSustain ? 1 : 0);
            scalarBytes[i * 8 + 5] = (sbyte)(b.StarPower ? 1 : 0);
            scalarBytes[i * 8 + 6] = (sbyte)(b.Solo ? 1 : 0);
            scalarBytes[i * 8 + 7] = (sbyte)Math.Clamp(b.RiffRepeat, 0, 100);
        }

        return new PreparedPart(songKey, style, tier, part.ToString(), $"{from}->{to}",
            [.. source], [.. candidates], scalarBytes, riffGroups, [.. labels]);
    }

    private static int[] RiffGroupIds(IReadOnlyList<BeatFrame> beats)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new int[beats.Count];
        for (int i = 0; i < beats.Count; i++)
        {
            if (!ids.TryGetValue(beats[i].RiffKey, out int id))
                ids[beats[i].RiffKey] = id = ids.Count;
            result[i] = id;
        }

        return result;
    }

    /// <summary>Whether the target looks generated rather than hand-made (§6.2): identical to Rules' draft.</summary>
    private static bool LooksGenerated(EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to)
    {
        try
        {
            var kind = ChartInstruments.KindOf(part);
            var draft = Reducer.Reduce(chart, part, from, to, FretReductionRules.Default(kind, to));
            var actual = chart.PartOf(part)![to].Notes;
            if (draft.Notes.Count != actual.Count)
                return false;
            var a = draft.Notes.Select(static n => n.Tick).Order().ToArray();
            var b = actual.AsSpan().ToArray().Select(static n => n.Tick).Order().ToArray();
            return a.SequenceEqual(b);
        }
        catch
        {
            return false;
        }
    }

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

    private static string SongKeyOf(string artist, string title)
    {
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Norm(artist) + "|" + Norm(title);
    }

    private static int? TierOf(Dictionary<string, string> ini, ChartInstrument part)
    {
        string key = part switch
        {
            ChartInstrument.Bass => "diff_bass",
            ChartInstrument.Keys => "diff_keys",
            ChartInstrument.Rhythm => "diff_rhythm",
            _ => "diff_guitar",
        };
        if (ini.TryGetValue(key, out string? value) && int.TryParse(value.Split(' ')[0], out int tier))
            return tier;
        return null;
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

    private const string ThisVersion = "1.0";
}
