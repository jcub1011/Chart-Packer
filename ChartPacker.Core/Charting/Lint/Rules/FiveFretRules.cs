using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint.Rules;

/// <summary>What the 5-fret and GHL rules share: which parts they check, and the chords the games read.</summary>
internal static class FretChords
{
    /// <summary>Whether <paramref name="part"/> is 5-fret or 6-fret (GHL), which these rules check.</summary>
    public static bool IsFrets(EditablePart part) => part.Kind is ChartInstrumentKind.FiveFret or ChartInstrumentKind.SixFret;

    /// <summary>The chord of <paramref name="track"/> before <paramref name="tick"/>, or none.</summary>
    public static ReadOnlySpan<EditableNote> ChordBefore(EditableTrack track, long tick)
    {
        var notes = track.Notes;
        int index = notes.FirstAtOrAfter(tick);
        return index == 0 ? [] : notes.At(notes[index - 1].Tick);
    }

    public static bool HasLane(ReadOnlySpan<EditableNote> chord, int lane)
    {
        foreach (var note in chord)
        {
            if (note.Lane == lane)
                return true;
        }

        return false;
    }

    /// <summary>Whether two chords have the same lanes; a track's chords are in lane order.</summary>
    public static bool SameLanes(ReadOnlySpan<EditableNote> a, ReadOnlySpan<EditableNote> b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Lane != b[i].Lane)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="chord"/> is a natural HOPO in a MIDI chart, as <see cref="NoteBuilder.Frets"/> reads it: a
    /// single note no more than <paramref name="threshold"/> ticks after the chord before, which isn't the same note or,
    /// after a chord, one of its notes.
    /// </summary>
    public static bool IsNaturalHopo(ReadOnlySpan<EditableNote> chord, ReadOnlySpan<EditableNote> before, long threshold) =>
        chord.Length == 1
        && !before.IsEmpty
        && chord[0].Tick - before[0].Tick <= threshold
        && !HasLane(before, chord[0].Lane);

    /// <summary>The location of the chord at <paramref name="tick"/> of <paramref name="track"/>.</summary>
    public static ChartLocation At(EditablePart part, EditableTrack track, long tick) => new(part.Instrument, track.Difficulty, tick, tick);
}

/// <summary>
/// CHR020: a sustain runs into the next note on its lane, so the games cut it short. Its location runs from the
/// sustain's note to that next note.
/// </summary>
public sealed class SustainIntoNextNoteRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr020;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>The next note on <paramref name="note"/>'s lane, if <paramref name="note"/>'s sustain runs into it.</summary>
    public static EditableNote? RunsInto(EditableTrack track, EditableNote note, long cutoff)
    {
        if (note.Length < cutoff)
            return null;
        var notes = track.Notes;
        long end = note.Tick + note.Length;
        for (int i = notes.FirstAfter(note.Tick); i < notes.Count && notes[i].Tick < end; i++)
        {
            if (notes[i].Lane == note.Lane)
                return notes[i];
        }

        return null;
    }

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        long cutoff = SustainCutoff.Of(context.Chart.Resolution, context.Options.SustainCutoff);
        var findings = new List<Finding>();
        var found = new HashSet<(long, long)>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                found.Clear();
                void Check(EditableNote note)
                {
                    if (RunsInto(track, note, cutoff) is { } next && found.Add((note.Tick, next.Tick)))
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrSustainIntoNext, Location: new ChartLocation(part.Instrument, track.Difficulty, note.Tick, next.Tick)));
                }

                // A sustain from before the region can reach into it: the last note on each lane before it is checked too.
                var notes = track.Notes;
                int first = notes.FirstAtOrAfter(context.FromTick);
                var lanes = new HashSet<int>();
                for (int i = first - 1; i >= 0 && lanes.Count < LaneCount(part); i--)
                {
                    if (lanes.Add(notes[i].Lane))
                        Check(notes[i]);
                }

                foreach (var note in context.NotesIn(track))
                    Check(note);
            }
        }

        return findings;
    }

    private static int LaneCount(EditablePart part) => EditableNote.LastLane(part.Kind) - EditableNote.FirstLane(part.Kind) + 1;
}

/// <summary>
/// CHR021: a sustain is shorter than the sustain cutoff, so the games read it as a plain note. Only lengths of at least a
/// sixteenth note count: shorter ones are plain notes as written, as many charts hold every note a sixteenth.
/// </summary>
public sealed class SustainUnderCutoffRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr021;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>Whether <paramref name="note"/> is held long enough to be meant as a sustain, but shorter than <paramref name="cutoff"/>.</summary>
    public static bool IsUnderCutoff(EditableNote note, int resolution, long cutoff) => note.Length >= resolution / 4 && note.Length < cutoff;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        int resolution = context.Chart.Resolution;
        long cutoff = SustainCutoff.Of(resolution, context.Options.SustainCutoff);
        if (cutoff <= resolution / 4)
            return [];

        string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrSustainUnderCutoffFormat, cutoff);
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var chord in context.ChordsIn(track))
                {
                    foreach (var note in chord)
                    {
                        if (IsUnderCutoff(note, resolution, cutoff))
                        {
                            findings.Add(new Finding(RuleId, Severity, message, Location: FretChords.At(part, track, note.Tick)));
                            break;
                        }
                    }
                }
            }
        }

        return findings;
    }
}

/// <summary>CHR022: an open note in a chord with frets, which the games read differently from what's written.</summary>
public sealed class OpenInChordRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr022;

    public Severity Severity => Severity.Error;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var chord in context.ChordsIn(track))
                {
                    if (chord.Length > 1 && FretChords.HasLane(chord, ChartNote.OpenLane))
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrOpenInChord, Location: FretChords.At(part, track, chord[0].Tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR023: a force HOPO or force strum that doesn't change the chord: forced HOPO, but a natural HOPO at every threshold
/// the games read a MIDI chart with; or forced strum, but a strum at every one. A tap is left alone.
/// </summary>
public sealed class RedundantForceRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr023;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Range;

    public int Reach => 1;

    /// <summary>The force the chord at <paramref name="tick"/> has that changes nothing, or none.</summary>
    public static ChordFlags RedundantForce(EditableTrack track, ReadOnlySpan<EditableNote> chord, ChartReadOptions options, int resolution)
    {
        var flags = track.FlagsAt(chord[0].Tick);
        if (flags.HasFlag(ChordFlags.Tap) || (flags & (ChordFlags.ForceHopo | ChordFlags.ForceStrum)) == 0)
            return ChordFlags.None;

        long[] thresholds = options.MidiHopoThresholds(resolution);
        var before = FretChords.ChordBefore(track, chord[0].Tick);
        if (flags.HasFlag(ChordFlags.ForceHopo))
            return FretChords.IsNaturalHopo(chord, before, thresholds.Min()) ? ChordFlags.ForceHopo : ChordFlags.None;
        return !FretChords.IsNaturalHopo(chord, before, thresholds.Max()) ? ChordFlags.ForceStrum : ChordFlags.None;
    }

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                if (track.FlaggedChords.Count == 0)
                    continue;
                foreach (var chord in context.ChordsIn(track))
                {
                    switch (RedundantForce(track, chord, context.Options, context.Chart.Resolution))
                    {
                        case ChordFlags.ForceHopo:
                            findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrRedundantForceHopo, Location: FretChords.At(part, track, chord[0].Tick)));
                            break;
                        case ChordFlags.ForceStrum:
                            findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrRedundantForceStrum, Location: FretChords.At(part, track, chord[0].Tick)));
                            break;
                    }
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR024: a forced HOPO on a chord that repeats the note or chord before it exactly. The games play it as a HOPO, but
/// the same frets can't be hammered on again.
/// </summary>
public sealed class ForcedRepeatRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr024;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 1;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                if (track.FlaggedChords.Count == 0)
                    continue;
                foreach (var chord in context.ChordsIn(track))
                {
                    long tick = chord[0].Tick;
                    var flags = track.FlagsAt(tick);
                    if (flags.HasFlag(ChordFlags.ForceHopo) && !flags.HasFlag(ChordFlags.Tap) && FretChords.SameLanes(chord, FretChords.ChordBefore(track, tick)))
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrForcedRepeat, Location: FretChords.At(part, track, tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>CHR025: the first note of a part's difficulty is a HOPO, forced or a tap, with nothing to hammer on from.</summary>
public sealed class FirstNoteHopoRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr025;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count == 0)
                    continue;
                long tick = track.Notes[0].Tick;
                if ((track.FlagsAt(tick) & (ChordFlags.ForceHopo | ChordFlags.Tap)) != 0)
                    findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrFirstNoteHopo, Location: FretChords.At(part, track, tick)));
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR026: two notes on the same lane closer than 1/64 of a whole note, the later probably a stray duplicate. Its
/// location runs from the first to the second.
/// </summary>
public sealed class CloseNotesRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr026;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>How close, in ticks, two notes on a lane are too close: under 1/64 of a whole note.</summary>
    public static long TooCloseTicks(int resolution) => resolution * 4L / 64;

    /// <summary>The next note on <paramref name="note"/>'s lane, if it's too close.</summary>
    public static EditableNote? TooCloseAfter(EditableTrack track, int index, long tooClose)
    {
        var notes = track.Notes;
        var note = notes[index];
        for (int i = index + 1; i < notes.Count && notes[i].Tick - note.Tick < tooClose; i++)
        {
            if (notes[i].Lane == note.Lane)
                return notes[i];
        }

        return null;
    }

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        long tooClose = TooCloseTicks(context.Chart.Resolution);
        var findings = new List<Finding>();
        foreach (var part in context.Parts.Where(FretChords.IsFrets))
        {
            foreach (var track in context.TracksOf(part))
            {
                var notes = track.Notes;

                // A pair can start before the region and end in it.
                int from = notes.FirstAtOrAfter(Math.Max(0, context.FromTick - tooClose + 1));
                int to = notes.FirstAfter(context.ToTick);
                for (int i = from; i < to; i++)
                {
                    if (TooCloseAfter(track, i, tooClose) is { } later && later.Tick >= context.FromTick)
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrCloseNotes, Location: new ChartLocation(part.Instrument, track.Difficulty, notes[i].Tick, later.Tick)));
                }
            }
        }

        return findings;
    }
}
