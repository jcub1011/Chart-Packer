using System.Globalization;
using ChartShared.Core.Charts;
using ChartShared.Core.Resources;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint.Rules;

/// <summary>What the drums rules share.</summary>
internal static class DrumParts
{
    /// <summary>The drums among the parts to check.</summary>
    public static IEnumerable<EditablePart> Of(ChartLintContext context) => context.Parts.Where(static p => p.Kind == ChartInstrumentKind.Drums);
}

/// <summary>CHR040: a tom marker that covers no note on its pad, on any difficulty, so it does nothing.</summary>
public sealed class TomMarkerCoversNothingRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr040;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>Whether <paramref name="marker"/> of <paramref name="drums"/> covers no note on its pad.</summary>
    public static bool CoversNothing(EditablePart drums, TomMarker marker) =>
        !DrumCymbals.HasPadNote(drums, marker.Pad, marker.Tick, DrumCymbals.CoverEnd(marker));

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            var markers = drums.TomMarkers;
            for (int i = 0; i < markers.Count && markers[i].Tick <= context.ToTick; i++)
            {
                var marker = markers[i];
                if (DrumCymbals.CoverEnd(marker) > context.FromTick && CoversNothing(drums, marker))
                    findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrTomMarkerCoversNothing, Location: new ChartLocation(drums.Instrument, null, marker.Tick, DrumCymbals.CoverEnd(marker) - 1)));
            }
        }

        return findings;
    }
}

/// <summary>CHR041: more than two pads hit at once, not counting the kick: more hands than a drummer has.</summary>
public sealed class TooManyPadsRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr041;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            foreach (var track in context.TracksOf(drums))
            {
                foreach (var chord in context.ChordsIn(track))
                {
                    int pads = 0;
                    foreach (var note in chord)
                    {
                        if (note.Lane > 0)
                            pads++;
                    }

                    if (pads > 2)
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrTooManyPads, Location: FretChords.At(drums, track, chord[0].Tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR042: a drum fill overlaps a star power phrase, where the player is earning star power rather than spending it. A
/// big rock ending isn't a fill, and isn't counted.
/// </summary>
public sealed class FillInStarPowerRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr042;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            var starPower = drums.Phrases.Where(static p => p.Kind == PhraseKind.StarPower).ToList();
            foreach (var fill in PhraseSpans.PhrasesIn(context, drums, PhraseKind.DrumFill))
            {
                if (starPower.Any(fill.Overlaps))
                    findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrFillInStarPower, Location: PhraseSpans.Span(drums, fill)));
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR043: a drum fill whose last note isn't on every difficulty that has notes, so there's nothing to activate star power
/// on there. The fill's last note is the last it covers on any difficulty (charter-spec C6 item 5). A big rock ending isn't
/// a fill, and isn't counted. One finding per difficulty without it.
/// </summary>
/// <remarks>It's a part rule, as whether a difficulty has notes at all depends on its whole track.</remarks>
public sealed class FillEndsWithoutNoteRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr043;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    /// <summary>The tick of the last note <paramref name="fill"/> covers on any difficulty of <paramref name="drums"/>, or null when it covers none.</summary>
    public static long? LastNote(EditablePart drums, Phrase fill)
    {
        long? last = null;
        foreach (var track in drums.Tracks)
        {
            var covered = PhraseSpans.Covered(track, fill);
            if (!covered.IsEmpty && (last is null || covered[^1].Tick > last))
                last = covered[^1].Tick;
        }

        return last;
    }

    /// <summary>
    /// The tick of the last note <paramref name="fill"/> covers that every difficulty with notes has, which the fill can
    /// end on; or null when there's none.
    /// </summary>
    public static long? LastSharedNote(EditablePart drums, Phrase fill)
    {
        var charted = drums.Tracks.Where(static t => t.Notes.Count > 0).ToList();
        if (charted.Count == 0)
            return null;
        var covered = PhraseSpans.Covered(charted[0], fill);
        for (int i = covered.Length - 1; i >= 0; i--)
        {
            long tick = covered[i].Tick;
            if (charted.All(t => !t.Notes.At(tick).IsEmpty))
                return tick;
        }

        return null;
    }

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            foreach (var fill in drums.Phrases.Where(static p => p.Kind == PhraseKind.DrumFill))
            {
                long? last = LastNote(drums, fill);
                foreach (var track in drums.Tracks)
                {
                    if (track.Notes.Count == 0 || (last is { } tick && !track.Notes.At(tick).IsEmpty))
                        continue;
                    string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrFillEndsWithoutNoteFormat, track.Difficulty);
                    findings.Add(new Finding(RuleId, Severity, message, Location: PhraseSpans.Span(drums, track, fill)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR044: a roll or trill with fewer than two notes in it, or a trill whose notes are all on one pad: on Expert, and on
/// each lower difficulty it reaches where it covers a note.
/// </summary>
public sealed class RollTooFewRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr044;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            var lanes = context.PhrasesIn(drums).Where(p => p.Kind is PhraseKind.Roll or PhraseKind.Trill && p.CoverEnd > context.FromTick);
            foreach (var lane in lanes)
            {
                foreach (var track in drums.Tracks)
                {
                    var covered = PhraseSpans.Covered(track, lane);
                    if (track.Difficulty < lane.LowestDifficulty || (track.Difficulty != ChartDifficulty.Expert && covered.IsEmpty))
                        continue;
                    string? message = covered.Length < 2
                        ? lane.Kind == PhraseKind.Roll ? FindingMessages.ChrRollTooFew : FindingMessages.ChrTrillTooFew
                        : lane.Kind == PhraseKind.Trill && OnePad(covered) ? FindingMessages.ChrTrillOnePad : null;
                    if (message is not null)
                        findings.Add(new Finding(RuleId, Severity, message, Location: PhraseSpans.Span(drums, track, lane)));
                }
            }
        }

        return findings;
    }

    private static bool OnePad(ReadOnlySpan<EditableNote> notes)
    {
        foreach (var note in notes)
        {
            if (note.Lane != notes[0].Lane)
                return false;
        }

        return true;
    }
}

/// <summary>
/// CHR045: 4-lane drums have tom markers. The games would read them as Pro drums, so saving leaves them out (charter-spec
/// C6 item 9). One finding, over the markers.
/// </summary>
public sealed class TomMarkersOnFourLaneRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr045;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (context.Chart.DrumsVariant != DrumsVariant.FourLane)
            return [];
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            var markers = drums.TomMarkers;
            if (markers.Count > 0)
            {
                long end = markers.Max(DrumCymbals.CoverEnd) - 1;
                findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrTomMarkersOnFourLane, Location: new ChartLocation(drums.Instrument, null, markers[0].Tick, end)));
            }
        }

        return findings;
    }
}

/// <summary>CHR046: a fifth-pad note (5-lane green) on drums that aren't 5-lane.</summary>
public sealed class FifthPadNotFiveLaneRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr046;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (context.Chart.DrumsVariant == DrumsVariant.FiveLane)
            return [];
        var findings = new List<Finding>();
        foreach (var drums in DrumParts.Of(context))
        {
            foreach (var track in context.TracksOf(drums))
            {
                foreach (var note in context.NotesIn(track))
                {
                    if (note.Lane == 5)
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrFifthPadNotFiveLane, Location: FretChords.At(drums, track, note.Tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>CHR048: the chart has a <c>PART DRUMS_2X</c> track, which is kept as it is and doesn't follow edits to <c>PART DRUMS</c>.</summary>
public sealed class Drums2xTrackRule : IChartLintRule
{
    /// <summary>The track's name.</summary>
    public const string TrackName = "PART DRUMS_2X";

    public string RuleId => ChrRules.Chr048;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context) =>
        context.Chart.Passthrough.Any(static t => t.Name == TrackName)
            ? [new Finding(RuleId, Severity, FindingMessages.ChrDrums2xTrack, Tick: 0)]
            : [];
}
