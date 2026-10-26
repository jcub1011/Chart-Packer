using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// The Neural generator (spec §3.3): BiGRU + CRF rhythm choice over beat patterns, voiced by
/// Rules' logic (§3.4 steps 5–6; learned voicing is M7). Falls back to Rules with a reason
/// whenever it can't run (§3.7).
/// </summary>
public sealed class NeuralDifficultyGenerator : IDifficultyGenerator
{
    public const string NeuralMethodId = "neural";

    private readonly IGeneratorModelStore _store;
    private readonly Func<ChartDifficulty, FretReductionRules> _voiceRules;
    private readonly Dictionary<string, (NeuralPack Pack, BiGruCrf Model)> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public string MethodId => NeuralMethodId;

    /// <param name="voiceRules">Rules' voicing and attributes per target (§3.4 steps 5–6).</param>
    public NeuralDifficultyGenerator(IGeneratorModelStore store, Func<ChartDifficulty, FretReductionRules> voiceRules)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _voiceRules = voiceRules ?? throw new ArgumentNullException(nameof(voiceRules));
    }

    /// <summary>Only 5-fret (keys among them) and GHL for now; drums keep Rules (§1.2, §3.7).</summary>
    public bool CanRunFor(ChartInstrumentKind kind) =>
        kind is ChartInstrumentKind.FiveFret or ChartInstrumentKind.SixFret;

    /// <summary>Installed neural packs, by model name.</summary>
    public IReadOnlyList<string> InstalledPacks() =>
        [.. _store.List()
            .Where(m => m.Id.Type == GeneratorType.NeuralDifficulty)
            .Select(m => m.Id.Name)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)];

    public ReductionResult? Draft(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        GenerationOptions options,
        (long From, long To)? range,
        ChartReadOptions? readOptions,
        out string? fallbackReason)
    {
        fallbackReason = null;
        var kind = ChartInstruments.KindOf(part);
        if (!CanRunFor(kind))
        {
            fallbackReason = $"Neural has no pack for {kind}; its draft is Rules'.";
            return null;
        }

        string? modelName = options.ModelName ?? InstalledPacks().FirstOrDefault();
        if (modelName is null)
        {
            fallbackReason = "No neural pack is installed; its draft is Rules'. Get packs to enable it.";
            return null;
        }

        NeuralPack pack;
        BiGruCrf model;
        try
        {
            (pack, model) = Load(modelName);
        }
        catch (PackValidationException ex)
        {
            fallbackReason = $"Neural pack '{modelName}' failed to load ({ex.Message}); its draft is Rules'.";
            return null;
        }

        string pair = $"{from}->{to}";
        if (!pack.Manifest.Pairs.Contains(pair, StringComparer.OrdinalIgnoreCase)
            && !pack.Manifest.Pairs.Contains($"Expert->{to}", StringComparer.OrdinalIgnoreCase))
        {
            fallbackReason = $"Neural pack '{modelName}' was not trained on {pair}; its draft is Rules'.";
            return null;
        }

        try
        {
            return ReduceWithModel(chart, part, from, to, pack, model, options, range);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fallbackReason = $"Neural failed ({ex.Message}); its draft is Rules'.";
            return null;
        }
    }

    private (NeuralPack Pack, BiGruCrf Model) Load(string modelName)
    {
        if (_loaded.TryGetValue(modelName, out var cached))
            return cached;
        var task = NeuralPack.LoadAsync(_store, new GeneratorModelId(GeneratorType.NeuralDifficulty, modelName));
        var pack = task.GetAwaiter().GetResult();
        var loaded = (pack, new BiGruCrf(pack));
        _loaded[modelName] = loaded;
        return loaded;
    }

    private ReductionResult ReduceWithModel(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        NeuralPack pack,
        BiGruCrf model,
        GenerationOptions options,
        (long From, long To)? range)
    {
        var whole = chart.PartOf(part)!;
        var source = whole[from];
        var target = whole[to];
        var kind = ChartInstruments.KindOf(part);

        var beats = BeatPipeline.Extract(chart, part, from, range);
        if (beats.Count == 0)
        {
            var empty = range is { } r
                ? target.Notes.AsSpan().ToArray().Where(n => n.Tick < r.From || n.Tick > r.To).ToList()
                : new List<EditableNote>();
            return new ReductionResult(empty, [], [], 0);
        }

        var conditioning = new RunConditioning(
            to, from, kind, StyleIdOf(pack, options.Style), BeatPipeline.TierBucketOf(TierOf(chart)));

        var candidateSets = beats
            .Select(b => new HashSet<int>(pack.Manifest.Vocabulary
                .Select((_, i) => i)
                .Where(i => BeatPattern.VocabularyOffsets[MapVocab(pack, i)]
                    .All(b.SourceOffsets48.Contains))))
            .ToList();

        var emissions = model.Emissions(beats, conditioning, candidateSets);

        long floor = options.MinGapTicks > 0
            ? options.MinGapTicks * chart.Resolution / BeatPattern.UnitsPerBeat
            : pack.Manifest.MinGapTicks.TryGetValue(to.ToString(), out long gap48)
                ? gap48 * chart.Resolution / BeatPattern.UnitsPerBeat
                : 0;

        var constraints = new ViterbiDecoder.DecodeConstraints(
            floor, RequireStarPowerSolo: true, options.DensityBias, candidateSets, beats);
        int[] choice = ViterbiDecoder.Decode(
            emissions, pack.Weights.Transition,
            pack.Weights.StartTransition, pack.Weights.EndTransition, constraints);

        return Voice(chart, part, from, to, kind, beats, choice, pack, range, options.CapLanes);
    }

    private ReductionResult Voice(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        ChartInstrumentKind kind,
        IReadOnlyList<BeatFrame> beats,
        int[] choice,
        NeuralPack pack,
        (long From, long To)? range,
        bool capLanes)
    {
        var whole = chart.PartOf(part)!;
        var source = whole[from];
        var target = whole[to];
        var byTick = source.Notes.GroupBy(static n => n.Tick).ToDictionary(static g => g.Key, static g => g.ToArray());
        var voiceRules = _voiceRules(to);

        int maxChord = Math.Max(1, voiceRules.MaxChordSize);
        // The lane cap: phrases are fitted into the target's usual range (§3.4 step 5), never
        // dropping onsets for it. The charter can lift it per run; keys keep the full range,
        // as official keys charts do (§5.4).
        int cap = !capLanes || part == ChartInstrument.Keys ? int.MaxValue : voiceRules.HighestLane;
        var shiftByTick = LaneShifts(chart, source, beats, choice, pack, range, cap);

        var notes = new List<EditableNote>();
        var flags = new List<FlaggedChord>();
        int removed = 0;

        for (int i = 0; i < beats.Count; i++)
        {
            var keepTicks = KeptTicks(beats[i], choice[i], pack);

            foreach (long tick in keepTicks.Order())
            {
                // A range reduction drafts only its range; the context beats around it are
                // decoded for context, never voiced (§3.6).
                if (range is { } bounds && (tick < bounds.From || tick > bounds.To))
                    continue;
                if (!byTick.TryGetValue(tick, out var chord))
                    continue;
                int shift = shiftByTick.GetValueOrDefault(tick);
                var placed = Narrow(chord, maxChord, shift);
                foreach (var note in placed)
                {
                    long length = note.Length;
                    if (length < chart.Resolution && !voiceRules.KeepShortSustains)
                        length = EditableNote.PlainLength;
                    notes.Add(note with { Length = length });
                }

                var marked = source.FlagsAt(tick);
                if (!voiceRules.KeepForcedHopoAndTap)
                    marked &= ~(ChordFlags.ForceHopo | ChordFlags.Tap);
                if (marked != ChordFlags.None)
                    flags.Add(new FlaggedChord(tick, marked));
            }

            int inRange = range is { } rr
                ? beats[i].SourceTicks.Count(t => t >= rr.From && t <= rr.To)
                : beats[i].SourceTicks.Length;
            int keptInRange = range is { } rr2
                ? keepTicks.Count(t => t >= rr2.From && t <= rr2.To)
                : keepTicks.Count;
            removed += inRange - keptInRange;
        }

        if (range is { } r)
        {
            notes.AddRange(target.Notes.AsSpan().ToArray().Where(n => n.Tick < r.From || n.Tick > r.To));
            flags.AddRange(target.FlaggedChords.AsSpan().ToArray().Where(f => f.Tick < r.From || f.Tick > r.To));
            flags.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
        }

        return new ReductionResult(notes, flags, [], Math.Max(0, removed));
    }

    private static HashSet<long> KeptTicks(BeatFrame beat, int choiceId, NeuralPack pack)
    {
        var slots = new HashSet<int>(BeatPattern.MapToSource(beat.SourceOffsets48, MapVocab(pack, choiceId)));
        var keep = new HashSet<long>();
        for (int k = 0; k < beat.SourceOffsets48.Length && k < beat.SourceTicks.Length; k++)
        {
            if (slots.Contains(beat.SourceOffsets48[k]))
                keep.Add(beat.SourceTicks[k]);
        }

        return keep;
    }

    /// <summary>
    /// How far each kept chord moves down to fit the target's lane range: per phrase, the whole
    /// phrase shifts down as one where it otherwise reaches past the cap, so its shape and the
    /// melody's contour survive. Onsets are never dropped for the cap; clamping collisions keep
    /// the longest sustain.
    /// </summary>
    private static Dictionary<long, int> LaneShifts(
        EditableChart chart,
        EditableTrack source,
        IReadOnlyList<BeatFrame> beats,
        int[] choice,
        NeuralPack pack,
        (long From, long To)? range,
        int cap)
    {
        var shifts = new Dictionary<long, int>();
        if (cap == int.MaxValue)
            return shifts;
        var chords = Reducer.ChordsOf([.. source.Notes.AsSpan().ToArray()]);
        if (chords.Count == 0)
            return shifts;
        var tickToChord = new Dictionary<long, int>();
        for (int i = 0; i < chords.Count; i++)
            tickToChord[chords[i].Tick] = i;
        var keptChords = new HashSet<int>();
        for (int i = 0; i < beats.Count; i++)
        {
            foreach (long tick in KeptTicks(beats[i], choice[i], pack))
            {
                if (range is { } r && (tick < r.From || tick > r.To))
                    continue;
                if (tickToChord.TryGetValue(tick, out int index))
                    keptChords.Add(index);
            }
        }

        foreach (var (start, end) in Reducer.Phrases(chords, Reducer.SectionTicks(chart), chart.Resolution))
        {
            int top = -1;
            for (int i = start; i <= end; i++)
            {
                if (!keptChords.Contains(i))
                    continue;
                foreach (var note in chords[i].Notes)
                {
                    if (note.Lane != ChartNote.OpenLane)
                        top = Math.Max(top, note.Lane);
                }
            }

            int shift = Math.Max(0, top - cap);
            if (shift == 0)
                continue;
            for (int i = start; i <= end; i++)
            {
                if (keptChords.Contains(i))
                    shifts[chords[i].Tick] = shift;
            }
        }

        return shifts;
    }

    private static EditableNote[] Narrow(EditableNote[] chord, int maxChord, int shift)
    {
        var pressed = chord.Where(static n => n.Lane != ChartNote.OpenLane).OrderBy(static n => n.Lane).ToList();
        var opens = chord.Where(static n => n.Lane == ChartNote.OpenLane).ToList();
        var kept = pressed.Take(Math.Max(1, maxChord)).ToList();
        kept.AddRange(opens);
        var keepSet = new HashSet<EditableNote>(kept);
        // Shift frets down into the lane range without dropping the onset; clamping collisions
        // keep the longest sustain.
        var placed = new List<EditableNote>();
        foreach (var note in chord.Where(keepSet.Contains))
        {
            int lane = note.Lane == ChartNote.OpenLane ? note.Lane : Math.Max(0, note.Lane - shift);
            int existing = placed.FindIndex(n => n.Lane == lane);
            if (existing < 0)
                placed.Add(note with { Lane = lane });
            else if (note.Length > placed[existing].Length)
                placed[existing] = note with { Lane = lane };
        }

        return [.. placed];
    }

    private static int StyleIdOf(NeuralPack pack, string style)
    {
        var match = pack.Manifest.Styles.FirstOrDefault(s =>
            string.Equals(s.Label, style, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match.Id;
        // Any (id 0) is the default (§3.3).
        return 0;
    }

    private static int? TierOf(EditableChart chart)
    {
        foreach (var e in chart.Events.AsSpan().ToArray())
        {
            // Charter spec: [tier N] / difficulty tag in EVENTS; accept "tier 3", "[tier 3]", "diff_guitar 4".
            string text = e.Text.Trim().Trim('[', ']').Trim();
            if (text.StartsWith("tier", StringComparison.OrdinalIgnoreCase))
            {
                var rest = text["tier".Length..].Trim();
                if (int.TryParse(rest.Split(' ')[0], out int tier))
                    return tier;
            }
        }

        return null;
    }

    private static int MapVocab(NeuralPack pack, int packedId)
    {
        // Pack vocabulary v1 matches BeatPattern.Vocabulary order (Train emits it in that order),
        // so ids coincide; labels are still matched defensively for future vocabularies.
        if (packedId < 0)
            return BeatPattern.EmptyId;
        if (packedId >= pack.Manifest.Vocabulary.Count)
            return BeatPattern.EmptyId;
        string label = pack.Manifest.Vocabulary[packedId];
        int index = Array.IndexOf(BeatPattern.Vocabulary, label);
        return index < 0 ? BeatPattern.EmptyId : index;
    }
}
