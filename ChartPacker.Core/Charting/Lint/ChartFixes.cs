using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Lint.Rules;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>
/// The quick fixes of the chart lint rules (charter-spec §6, C16 item 3), each one undoable command worked out from
/// the chart as it is when it's asked for. A fix that has no single right answer, such as a note snapped onto another,
/// proposes nothing.
/// </summary>
public static class ChartFixes
{
    /// <summary>Every chart fix, at most one per rule, for <see cref="QuickFixRegistry.ChartStrategies"/>.</summary>
    public static IReadOnlyList<IChartQuickFixStrategy> All { get; } =
    [
        new Fix(ChrRules.Chr001, AddFourFourAtStart),
        new Fix(ChrRules.Chr004, SnapToGrid),
        new Fix(ChrRules.Chr005, KeepTheLaterMarker),
        new Fix(ChrRules.Chr020, TrimSustain),
        new Fix(ChrRules.Chr021, RemoveSustain),
        new Fix(ChrRules.Chr022, RemoveOpen),
        new Fix(ChrRules.Chr023, RemoveRedundantForce),
        new Fix(ChrRules.Chr024, RemoveForceHopo),
        new Fix(ChrRules.Chr025, RemoveFirstForce),
        new Fix(ChrRules.Chr026, DeleteTheLaterNote),
        new Fix(ChrRules.Chr040, RemoveTomMarker),
        new Fix(ChrRules.Chr043, EndFillOnSharedNote),
        new Fix(ChrRules.Chr045, SwitchToPro),
        new Fix(ChrRules.Chr060, DeleteEmptyStarPower),
        new Fix(ChrRules.Chr061, DeleteEmptySolo),
        new Fix(ChrRules.Chr062, MergePhrases),
    ];

    // CHR001: 4/4 at 0, which the games assume without one.
    private static IChartCommand? AddFourFourAtStart(Finding finding, EditableChart chart, ChartReadOptions options) =>
        chart.TempoMap.TimeSignatures.At(0).IsEmpty ? new AddTimeSignature(new TimeSignatureMarker(0, 4, 4)) : null;

    // CHR004: the chord moved to the nearest tick on a common grid, unless a note is already there on one of its lanes.
    private static IChartCommand? SnapToGrid(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        var chord = track.Notes.At(location.Tick).ToArray();
        if (chord.Length == 0)
            return null;
        long target = OffGridRule.NearestOnGrid(location.Tick, chart.TempoMap.ToTempoMap());
        if (target == location.Tick || chord.Any(n => track.TryGetNote(target, n.Lane, n.DoubleKick, out _)))
            return null;
        return new MoveNotes(part.Instrument, track.Difficulty, chord, target - location.Tick, 0);
    }

    // CHR005: the markers before the last on the tick removed, as the games read only the last.
    private static IChartCommand? KeepTheLaterMarker(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (finding.Tick is not { } tick)
            return null;
        var map = chart.TempoMap;
        var tempos = map.Tempos.At(tick).ToArray();
        var signatures = map.TimeSignatures.At(tick).ToArray();
        var removes = new List<IChartCommand>();
        if (tempos.Length > 1)
            removes.AddRange(tempos[..^1].Select(m => (IChartCommand)new RemoveTempo(m)));
        if (signatures.Length > 1)
            removes.AddRange(signatures[..^1].Select(m => (IChartCommand)new RemoveTimeSignature(m)));
        return removes.Count switch
        {
            0 => null,
            1 => removes[0],
            _ => new CompositeCommand(removes),
        };
    }

    // CHR020: each sustain at the tick that runs into the next note on its lane, held to that note.
    private static IChartCommand? TrimSustain(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        long cutoff = SustainCutoff.Of(chart.Resolution, options.SustainCutoff);
        var lengths = track.Notes.At(location.Tick).ToArray()
            .Select(n => (Note: n, Next: SustainIntoNextNoteRule.RunsInto(track, n, cutoff)))
            .Where(p => p.Next is { } next && next.Tick == location.EndTick)
            .Select(p => (p.Note, p.Next!.Value.Tick - p.Note.Tick))
            .ToList();
        return lengths.Count > 0 ? new SetSustain(part.Instrument, track.Difficulty, lengths) : null;
    }

    // CHR021: the chord's sustains that the games read as plain notes made plain notes.
    private static IChartCommand? RemoveSustain(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        long cutoff = SustainCutoff.Of(chart.Resolution, options.SustainCutoff);
        var notes = track.Notes.At(location.Tick).ToArray().Where(n => SustainUnderCutoffRule.IsUnderCutoff(n, chart.Resolution, cutoff)).ToList();
        return notes.Count > 0 ? new SetSustain(part.Instrument, track.Difficulty, notes, EditableNote.PlainLength) : null;
    }

    // CHR022: the open note taken out of the chord, leaving its frets.
    private static IChartCommand? RemoveOpen(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        var chord = track.Notes.At(location.Tick).ToArray();
        var opens = chord.Where(n => n.Lane == ChartNote.OpenLane).ToList();
        return opens.Count > 0 && opens.Count < chord.Length ? new RemoveNotes(part.Instrument, track.Difficulty, opens) : null;
    }

    // CHR023: the force that changes nothing turned off.
    private static IChartCommand? RemoveRedundantForce(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        var chord = track.Notes.At(location.Tick);
        if (chord.IsEmpty)
            return null;
        var force = RedundantForceRule.RedundantForce(track, chord, options, chart.Resolution);
        return force == ChordFlags.None ? null : new SetChordFlags(part.Instrument, track.Difficulty, [location.Tick], force, on: false);
    }

    // CHR024: the forced HOPO turned off, so the repeated chord is strummed.
    private static IChartCommand? RemoveForceHopo(Finding finding, EditableChart chart, ChartReadOptions options) =>
        TrackAt(finding, chart) is var (part, track, location) && track.FlagsAt(location.Tick).HasFlag(ChordFlags.ForceHopo) && !track.Notes.At(location.Tick).IsEmpty
            ? new SetChordFlags(part.Instrument, track.Difficulty, [location.Tick], ChordFlags.ForceHopo, on: false)
            : null;

    // CHR025: the first chord's force HOPO and tap turned off.
    private static IChartCommand? RemoveFirstForce(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location) || track.Notes.At(location.Tick).IsEmpty)
            return null;
        var flags = track.FlagsAt(location.Tick) & (ChordFlags.ForceHopo | ChordFlags.Tap);
        return flags == ChordFlags.None ? null : new SetChordFlags(part.Instrument, track.Difficulty, [location.Tick], flags, on: false);
    }

    // CHR026: the later of the two notes deleted.
    private static IChartCommand? DeleteTheLaterNote(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (TrackAt(finding, chart) is not var (part, track, location))
            return null;
        long tooClose = CloseNotesRule.TooCloseTicks(chart.Resolution);
        var notes = track.Notes;
        for (int i = notes.FirstAtOrAfter(location.Tick); i < notes.Count && notes[i].Tick == location.Tick; i++)
        {
            if (CloseNotesRule.TooCloseAfter(track, i, tooClose) is { } later && later.Tick == location.EndTick)
                return new RemoveNotes(part.Instrument, track.Difficulty, [later]);
        }

        return null;
    }

    // CHR040: the marker that covers nothing removed.
    private static IChartCommand? RemoveTomMarker(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (finding.Location is not { } location || chart.PartOf(location.Part) is not { Kind: ChartInstrumentKind.Drums } drums)
            return null;
        var markers = drums.TomMarkers
            .Where(m => m.Tick == location.Tick && DrumCymbals.CoverEnd(m) - 1 == location.EndTick && TomMarkerCoversNothingRule.CoversNothing(drums, m))
            .ToList();
        return markers.Count > 0 ? new RemoveTomMarkers(markers) : null;
    }

    // CHR043: the fill ended on its last note that every difficulty with notes has.
    private static IChartCommand? EndFillOnSharedNote(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (finding.Location is not { } location || chart.PartOf(location.Part) is not { Kind: ChartInstrumentKind.Drums } drums)
            return null;
        foreach (var fill in drums.Phrases.Where(p => p.Kind == PhraseKind.DrumFill && p.Tick == location.Tick && p.CoverEnd - 1 == location.EndTick))
        {
            if (FillEndsWithoutNoteRule.LastSharedNote(drums, fill) is { } last && last + 1 - fill.Tick != fill.Length)
                return new ChangePhrase(drums.Instrument, fill, fill with { Length = last + 1 - fill.Tick });
        }

        return null;
    }

    // CHR045: the drums made Pro, whose tom markers are saved.
    private static IChartCommand? SwitchToPro(Finding finding, EditableChart chart, ChartReadOptions options) =>
        chart.DrumsVariant == DrumsVariant.FourLane ? new SetDrumsVariant(DrumsVariant.Pro) : null;

    // CHR060: the star power phrase with no notes deleted.
    private static IChartCommand? DeleteEmptyStarPower(Finding finding, EditableChart chart, ChartReadOptions options) =>
        DeleteEmpty(finding, chart, PhraseKind.StarPower);

    // CHR061: the solo with no notes deleted.
    private static IChartCommand? DeleteEmptySolo(Finding finding, EditableChart chart, ChartReadOptions options) =>
        DeleteEmpty(finding, chart, PhraseKind.Solo);

    private static RemovePhrase? DeleteEmpty(Finding finding, EditableChart chart, PhraseKind kind)
    {
        if (finding.Location is not { } location || chart.PartOf(location.Part) is not { } part)
            return null;
        foreach (var phrase in part.Phrases.Where(p => p.Kind == kind && p.Tick == location.Tick && p.CoverEnd - 1 == location.EndTick))
        {
            if (EmptyStarPowerRule.IsEmpty(part, phrase))
                return new RemovePhrase(part.Instrument, phrase);
        }

        return null;
    }

    // CHR062: the overlapping phrases merged into the first, from its start to the last end, as saving would merge them.
    private static IChartCommand? MergePhrases(Finding finding, EditableChart chart, ChartReadOptions options)
    {
        if (finding.Location is not { } location || chart.PartOf(location.Part) is not { } part)
            return null;
        foreach (var kind in (PhraseKind[])[PhraseKind.StarPower, PhraseKind.Solo])
        {
            foreach (var group in OverlappingPhrasesRule.Groups(part, kind))
            {
                if (OverlappingPhrasesRule.SpanOf(part, group) != location)
                    continue;
                var first = group[0];
                var commands = group.Skip(1).Select(p => (IChartCommand)new RemovePhrase(part.Instrument, p)).ToList();
                long end = group.Max(static p => p.Tick + p.Length);
                if (end - first.Tick != first.Length)
                    commands.Add(new ChangePhrase(part.Instrument, first, first with { Length = end - first.Tick }));
                return new CompositeCommand(commands);
            }
        }

        return null;
    }

    /// <summary>The part and track a finding about one difficulty is on, and its location; null when there's none.</summary>
    private static (EditablePart Part, EditableTrack Track, ChartLocation Location)? TrackAt(Finding finding, EditableChart chart) =>
        finding.Location is { Difficulty: { } difficulty } location && chart.PartOf(location.Part) is { } part
            ? (part, part[difficulty], location)
            : null;

    /// <summary>A chart fix for one rule, from a function.</summary>
    private sealed class Fix(string ruleId, Func<Finding, EditableChart, ChartReadOptions, IChartCommand?> propose) : IChartQuickFixStrategy
    {
        public string RuleId => ruleId;

        public IChartCommand? Propose(Finding finding, EditableChart chart, ChartReadOptions options)
        {
            ArgumentNullException.ThrowIfNull(finding);
            ArgumentNullException.ThrowIfNull(chart);
            ArgumentNullException.ThrowIfNull(options);
            return propose(finding, chart, options);
        }
    }
}
