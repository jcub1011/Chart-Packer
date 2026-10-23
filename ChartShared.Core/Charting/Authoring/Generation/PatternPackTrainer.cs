using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Counts a prepared set of charts into a pattern pack (spec 6.4).
/// Training is counting: for every beat, the context and the official outcome.</summary>
public static class PatternPackTrainer
{
    public sealed record TrainOptions(
        string Name = "Trained pack",
        string Author = "",
        string Description = "",
        string License = "",
        int MaxVocabulary = 256);

    public sealed record TrainResult(PatternPack Pack, int Songs, int Parts, int Beats, IReadOnlyList<string> Warnings);

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
        ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Keys,
        ChartInstrument.Rhythm, ChartInstrument.GuitarCoop,
    ];

    private readonly record struct RawContextKey(
        byte Level,
        ulong Source,
        byte Target,
        byte SourceDiff,
        byte Shape,
        ulong Prev1,
        ulong Prev2,
        ulong Prev3,
        ulong NextSource,
        byte MeasurePlace,
        byte StyleId);

    /// <summary>Train from already-read charts. <paramref name="songs"/> is (document, style, songKey).</summary>
    public static TrainResult Train(IReadOnlyList<(ChartDocument Doc, string Style, string SongKey)> songs, TrainOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(songs);
        options ??= new TrainOptions();
        var rawCounts = new Dictionary<RawContextKey, Dictionary<ulong, int>>(1 << 16);
        var maskFrequency = new Dictionary<ulong, int>();
        var seenParts = new HashSet<string>(StringComparer.Ordinal);
        var styles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var styleIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { ["Any"] = 0 };
        var styleById = new List<string> { "Any" };
        var pairsSeen = new SortedSet<string>(StringComparer.Ordinal);
        int parts = 0, beats = 0;
        var warnings = new List<string>();

        void AddCount(in RawContextKey key, ulong targetMask)
        {
            if (!rawCounts.TryGetValue(key, out var row))
                rawCounts[key] = row = new Dictionary<ulong, int>(4);
            row[targetMask] = row.TryGetValue(targetMask, out int c) ? c + 1 : 1;
        }

        foreach (var (doc, style, songKey) in songs)
        {
            string normStyle = string.IsNullOrWhiteSpace(style) ? "Any" : style.Trim();
            if (!styleIds.TryGetValue(normStyle, out byte styleId))
            {
                styleId = (byte)Math.Min(255, styleById.Count);
                styleIds[normStyle] = styleId;
                styleById.Add(normStyle);
            }

            var map = doc.Tempo;
            foreach (var part in FiveFretParts)
            {
                if (!doc.Tracks.Keys.Any(k => k.Instrument == part))
                    continue;
                var byDiff = new Dictionary<ChartDifficulty, NoteTrack>(4);
                foreach (var kv in doc.Tracks)
                {
                    if (kv.Key.Instrument == part && kv.Value.Notes.Count > 0)
                        byDiff[kv.Key.Difficulty] = kv.Value;
                }

                if (byDiff.Count < 2)
                    continue;

                var beatsByDiff = new Dictionary<ChartDifficulty, IReadOnlyList<SourceBeat>>(byDiff.Count);
                var hashesByDiff = new Dictionary<ChartDifficulty, string>(byDiff.Count);

                foreach (var (from, to) in Pairs)
                {
                    if (!byDiff.TryGetValue(from, out var sourceTrack) || !byDiff.TryGetValue(to, out var targetTrack))
                        continue;
                    if (!hashesByDiff.TryGetValue(from, out string? fromHash))
                        hashesByDiff[from] = fromHash = HashNotes(sourceTrack.Notes);
                    if (!hashesByDiff.TryGetValue(to, out string? toHash))
                        hashesByDiff[to] = toHash = HashNotes(targetTrack.Notes);

                    string dedupe = $"{songKey}|{part}|{from}->{to}|{fromHash}|{toHash}";
                    if (!seenParts.Add(dedupe))
                        continue; // Duplicates counted once (spec 6.2).

                    if (!beatsByDiff.TryGetValue(from, out var sourceBeats))
                        beatsByDiff[from] = sourceBeats = BeatTable.Build(sourceTrack.Notes, map);

                    var targetBeats = TargetMasks(targetTrack, sourceBeats);
                    byte tgtByte = (byte)to;
                    byte srcDiffByte = (byte)from;

                    for (int i = 0; i < sourceBeats.Count; i++)
                    {
                        var beat = sourceBeats[i];
                        if (beat.Source.IsEmpty)
                            continue; // Empty source beats are context only (spec 3.4.1).

                        var target = targetBeats[i];
                        ulong srcMask = beat.Source.Mask;
                        ulong tgtMask = target.Mask;
                        ulong p1 = i >= 1 ? targetBeats[i - 1].Mask : 0UL;
                        ulong p2 = i >= 2 ? targetBeats[i - 2].Mask : 0UL;
                        ulong p3 = i >= 3 ? targetBeats[i - 3].Mask : 0UL;
                        ulong nextSrc = i + 1 < sourceBeats.Count ? sourceBeats[i + 1].Source.Mask : 0UL;
                        byte pos = (byte)Math.Clamp(beat.MeasurePlace, 0, 31);
                        byte sh = (byte)beat.Shape;

                        AddCount(new RawContextKey(0, srcMask, 0, 0, 0, 0, 0, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(1, srcMask, tgtByte, 0, 0, 0, 0, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(2, srcMask, tgtByte, srcDiffByte, 0, 0, 0, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(3, srcMask, tgtByte, srcDiffByte, sh, 0, 0, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(4, srcMask, tgtByte, srcDiffByte, sh, p1, 0, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(5, srcMask, tgtByte, srcDiffByte, sh, p1, p2, 0, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(6, srcMask, tgtByte, srcDiffByte, sh, p1, p2, p3, 0, 0, 0), tgtMask);
                        AddCount(new RawContextKey(7, srcMask, tgtByte, srcDiffByte, sh, p1, p2, p3, nextSrc, 0, 0), tgtMask);
                        AddCount(new RawContextKey(8, srcMask, tgtByte, srcDiffByte, sh, p1, p2, p3, nextSrc, pos, 0), tgtMask);
                        if (styleId != 0)
                            AddCount(new RawContextKey(9, srcMask, tgtByte, srcDiffByte, sh, p1, p2, p3, nextSrc, pos, styleId), tgtMask);

                        maskFrequency[srcMask] = maskFrequency.TryGetValue(srcMask, out int s) ? s + 1 : 1;
                        maskFrequency[tgtMask] = maskFrequency.TryGetValue(tgtMask, out int t) ? t + 1 : 1;
                        beats++;
                    }

                    parts++;
                    styles.Add(normStyle);
                    pairsSeen.Add($"{from}->{to}");
                }
            }
        }

        if (parts == 0)
            throw new InvalidOperationException("No usable parts: need 5-fret parts with source and target difficulties charted.");

        var vocab = maskFrequency.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key)
            .Take(Math.Max(1, options.MaxVocabulary))
            .Select(static kv => new BeatPattern(kv.Key)).ToList();
        if (!vocab.Contains(BeatPattern.Empty))
            vocab.Insert(0, BeatPattern.Empty);

        bool prune = beats >= 1000;
        var counts = new Dictionary<string, IReadOnlyDictionary<ulong, int>>(
            prune ? Math.Min(rawCounts.Count, 250_000) : rawCounts.Count,
            StringComparer.Ordinal);

        long singletons = 0;
        foreach (var (rawKey, row) in rawCounts)
        {
            long rowTotal = 0;
            foreach (int c in row.Values)
                rowTotal += c;
            if (rowTotal <= 1)
                singletons++;

            if (prune)
            {
                int minCount = rawKey.Level switch
                {
                    0 or 1 or 2 => 1,
                    3 => 2,
                    4 => 4,
                    5 => 8,
                    6 => 16,
                    7 => 25,
                    8 => 35,
                    _ => 50,
                };
                if (rowTotal < minCount)
                    continue;

                if (rowTotal >= 50 && row.Count > 2)
                {
                    var trimmed = new Dictionary<ulong, int>(row.Count);
                    foreach (var (m, c) in row)
                    {
                        if (c > 1)
                            trimmed[m] = c;
                    }

                    if (trimmed.Count > 0)
                    {
                        counts[FormatKey(in rawKey, styleById)] = trimmed;
                        continue;
                    }
                }
            }

            counts[FormatKey(in rawKey, styleById)] = row;
        }

        if (singletons * 2 > rawCounts.Count && rawCounts.Count > 0 && !prune)
            warnings.Add($"{singletons} of {rawCounts.Count} contexts seen once: data is thin for this context set (spec 6.2).");
        if (parts < 100)
            warnings.Add($"Only {parts} parts: the study's reference is about 5,000 parts per target (spec 6.2).");

        var minGaps = new Dictionary<string, long>
        {
            [nameof(ChartDifficulty.Hard)] = 80,
            [nameof(ChartDifficulty.Medium)] = 120,
            [nameof(ChartDifficulty.Easy)] = 160,
        };

        var manifest = new PatternPackManifest(
            PatternPack.CurrentFormatVersion,
            BeatPattern.SchemaVersion,
            options.Name,
            options.Author,
            options.Description,
            [.. styles.Select(static s => new PatternStyle(s, s)), new PatternStyle("Any", "Blend of all styles.")],
            true,
            ["FiveFret"],
            [.. pairsSeen],
            [.. PatternPackLoader.DefaultFeatures],
            [.. PatternPackLoader.DefaultBackoffOrder],
            minGaps,
            options.License,
            "");
        string checksum = PatternPackLoader.ChecksumOf(vocab, counts);
        manifest = manifest with { Checksum = checksum };
        var pack = new PatternPack(manifest, vocab, counts);
        return new TrainResult(pack, songs.Count, parts, beats, warnings);
    }

    private static string FormatKey(in RawContextKey k, List<string> styleById)
    {
        string src = new BeatPattern(k.Source).ToDisplay();
        if (k.Level == 0)
            return $"src={src}";
        string tgt = ((ChartDifficulty)k.Target).ToString();
        if (k.Level == 1)
            return $"src={src}|tgt={tgt}";
        string srcD = ((ChartDifficulty)k.SourceDiff).ToString();
        if (k.Level == 2)
            return $"src={src}|tgt={tgt}|srcD={srcD}";
        string sh = $"src={src}|tgt={tgt}|srcD={srcD}|sh={k.Shape}";
        if (k.Level == 3)
            return sh;
        string p1 = $"{sh}|p1={new BeatPattern(k.Prev1).ToDisplay()}";
        if (k.Level == 4)
            return p1;
        string p2 = $"{p1}|p2={new BeatPattern(k.Prev2).ToDisplay()}";
        if (k.Level == 5)
            return p2;
        string p3 = $"{p2}|p3={new BeatPattern(k.Prev3).ToDisplay()}";
        if (k.Level == 6)
            return p3;
        string next = $"{p3}|next={new BeatPattern(k.NextSource).ToDisplay()}";
        if (k.Level == 7)
            return next;
        string pos = $"{next}|pos={k.MeasurePlace}";
        if (k.Level == 8)
            return pos;
        return $"{pos}|style={styleById[k.StyleId]}";
    }

    /// <summary>Train from a folder of songs: each song folder's notes.mid/notes.chart (spec 6.2).</summary>
    public static TrainResult TrainFromFolder(string root, TrainOptions? options = null, Func<string, string>? styleOf = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var mids = Directory.EnumerateFiles(root, "notes.mid", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "notes.chart", SearchOption.AllDirectories)).ToList();
        var songs = new System.Collections.Concurrent.ConcurrentBag<(ChartDocument Doc, string Style, string SongKey)>();
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.ForEach(mids, parallelOptions, mid =>
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(mid);
            }
            catch
            {
                return;
            }

            ChartDocument doc;
            try
            {
                doc = ChartReader.Read(bytes, Path.GetFileName(mid));
            }
            catch
            {
                return;
            }

            string dir = Path.GetDirectoryName(mid) ?? root;
            string style = styleOf?.Invoke(dir)
                ?? Path.GetFileName(Path.GetDirectoryName(dir) ?? dir);
            string songKey = dir;
            songs.Add((doc, string.IsNullOrWhiteSpace(style) ? "Any" : style, songKey));
        });

        if (songs.IsEmpty)
            throw new InvalidOperationException($"No notes.mid/notes.chart found under {root}.");
        var orderedSongs = songs.OrderBy(static s => s.SongKey, StringComparer.Ordinal).ToList();
        return Train(orderedSongs, options);
    }

    private static List<BeatPattern> TargetMasks(NoteTrack target, IReadOnlyList<SourceBeat> sourceBeats)
    {
        var masks = new ulong[sourceBeats.Count];
        var starts = new long[sourceBeats.Count];
        for (int i = 0; i < sourceBeats.Count; i++)
            starts[i] = sourceBeats[i].StartTick;

        long lastTick = -1;
        foreach (var note in target.Notes)
        {
            if (note.Tick == lastTick)
                continue;
            lastTick = note.Tick;

            int idx = Array.BinarySearch(starts, note.Tick);
            if (idx < 0)
                idx = ~idx - 1;
            if (idx < 0 || idx >= sourceBeats.Count)
                continue;
            var beat = sourceBeats[idx];
            if (beat.Source.IsEmpty || note.Tick >= beat.StartTick + beat.LengthTicks)
                continue;
            int p = BeatTable.Offset48(note.Tick - beat.StartTick, beat.LengthTicks);
            ulong bit = 1UL << p;
            if ((beat.Source.Mask & bit) != 0)
            {
                masks[idx] |= bit;
            }
            else
            {
                // Snap slightly shifted target onsets (within 2 48ths = 1/24 beat) onto the nearest source onset (study §5.1).
                int bestOffset = -1;
                int bestDist = 3;
                for (int k = 0; k < beat.Offsets48.Count; k++)
                {
                    int dist = Math.Abs(beat.Offsets48[k] - p);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestOffset = beat.Offsets48[k];
                    }
                }

                if (bestOffset >= 0)
                    masks[idx] |= 1UL << bestOffset;
            }
        }

        var result = new List<BeatPattern>(sourceBeats.Count);
        for (int i = 0; i < masks.Length; i++)
            result.Add(new BeatPattern(masks[i] & sourceBeats[i].Source.Mask));
        return result;
    }

    private static string HashNotes(IReadOnlyList<ChartNote> notes)
    {
        unchecked
        {
            int h = 17;
            foreach (var n in notes)
            {
                h = h * 31 + n.Tick.GetHashCode();
                h = h * 31 + n.Lane;
            }

            return h.ToString("x8");
        }
    }
}
