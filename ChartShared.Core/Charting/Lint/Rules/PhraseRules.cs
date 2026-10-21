using System.Globalization;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Resources;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint.Rules;

/// <summary>What the phrase rules share: the ticks a phrase covers, as the games read it (<see cref="Phrase.CoverEnd"/>).</summary>
internal static class PhraseSpans
{
    /// <summary>The span <paramref name="phrase"/> covers, on every difficulty of <paramref name="part"/>.</summary>
    public static ChartLocation Span(EditablePart part, Phrase phrase) => new(part.Instrument, null, phrase.Tick, phrase.CoverEnd - 1);

    /// <summary>The span <paramref name="phrase"/> covers on <paramref name="track"/>.</summary>
    public static ChartLocation Span(EditablePart part, EditableTrack track, Phrase phrase) => new(part.Instrument, track.Difficulty, phrase.Tick, phrase.CoverEnd - 1);

    /// <summary>The notes of <paramref name="track"/> that <paramref name="phrase"/> covers.</summary>
    public static ReadOnlySpan<EditableNote> Covered(EditableTrack track, Phrase phrase) => track.Notes.Between(phrase.Tick, phrase.CoverEnd - 1);

    /// <summary>The phrases of <paramref name="part"/> of <paramref name="kind"/> that cover a tick of the context's region.</summary>
    public static IEnumerable<Phrase> PhrasesIn(ChartLintContext context, EditablePart part, PhraseKind kind) =>
        context.PhrasesIn(part).Where(p => p.Kind == kind && p.CoverEnd > context.FromTick);
}

/// <summary>CHR060: a star power phrase with no notes in it on any difficulty.</summary>
public sealed class EmptyStarPowerRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr060;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>Whether <paramref name="phrase"/> of <paramref name="part"/> covers no note on any difficulty.</summary>
    public static bool IsEmpty(EditablePart part, Phrase phrase) => part.Tracks.All(t => PhraseSpans.Covered(t, phrase).IsEmpty);

    public IEnumerable<Finding> Check(ChartLintContext context) => Empty(this, context, PhraseKind.StarPower, FindingMessages.ChrEmptyStarPower);

    /// <summary>The findings of <paramref name="rule"/>: each phrase of <paramref name="kind"/> in the context's region with no notes in it.</summary>
    internal static List<Finding> Empty(IChartLintRule rule, ChartLintContext context, PhraseKind kind, string message)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var phrase in PhraseSpans.PhrasesIn(context, part, kind))
            {
                if (IsEmpty(part, phrase))
                    findings.Add(new Finding(rule.RuleId, rule.Severity, message, Location: PhraseSpans.Span(part, phrase)));
            }
        }

        return findings;
    }
}

/// <summary>CHR061: a solo with no notes in it on any difficulty.</summary>
public sealed class EmptySoloRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr061;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context) => EmptyStarPowerRule.Empty(this, context, PhraseKind.Solo, FindingMessages.ChrEmptySolo);
}

/// <summary>
/// CHR062: star power phrases, or solos, that overlap, which saving merges into one (see
/// <see cref="MidiChartWriter.Normalize"/>). One finding for each group saving would merge, over the group.
/// </summary>
public sealed class OverlappingPhrasesRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr062;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>
    /// The groups of phrases of <paramref name="kind"/> in <paramref name="part"/> that overlap, each two or more phrases
    /// that saving would merge into one, in order: each overlaps one before it in its group.
    /// </summary>
    public static IEnumerable<IReadOnlyList<Phrase>> Groups(EditablePart part, PhraseKind kind)
    {
        var group = new List<Phrase>();
        long end = long.MinValue;
        foreach (var phrase in part.Phrases)
        {
            if (phrase.Kind != kind)
                continue;
            if (group.Count > 0 && phrase.Tick < end)
            {
                group.Add(phrase);
                end = Math.Max(end, phrase.CoverEnd);
                continue;
            }

            if (group.Count > 1)
                yield return group;
            group = [phrase];
            end = phrase.CoverEnd;
        }

        if (group.Count > 1)
            yield return group;
    }

    /// <summary>The span <paramref name="group"/> covers between them, on every difficulty of <paramref name="part"/>.</summary>
    public static ChartLocation SpanOf(EditablePart part, IReadOnlyList<Phrase> group) =>
        new(part.Instrument, null, group[0].Tick, group.Max(static p => p.CoverEnd) - 1);

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var group in Groups(part, PhraseKind.StarPower))
                findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrOverlappingStarPower, Location: SpanOf(part, group)));
            foreach (var group in Groups(part, PhraseKind.Solo))
                findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrOverlappingSolos, Location: SpanOf(part, group)));
        }

        return findings;
    }
}

/// <summary>
/// CHR063: notes inside a big rock ending, which the games skip. One finding for each difficulty with a note in it.
/// </summary>
public sealed class NotesInBigRockEndingRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr063;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var ending in PhraseSpans.PhrasesIn(context, part, PhraseKind.BigRockEnding))
            {
                foreach (var track in part.Tracks)
                {
                    if (!PhraseSpans.Covered(track, ending).IsEmpty)
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrNotesInBigRockEnding, Location: PhraseSpans.Span(part, track, ending)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR065: a star power phrase or solo with no notes in it on a charted difficulty, though another difficulty has some, as
/// a reduction can leave one. One finding for each such difficulty; a phrase empty on every difficulty is CHR060's or
/// CHR061's.
/// </summary>
public sealed class PhraseEmptyOnDifficultyRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr065;

    public Severity Severity => Severity.Warning;

    // Part, not range: a difficulty getting its first note, or losing its last, changes its findings anywhere.
    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            Add(part, PhraseKind.StarPower, FindingMessages.ChrStarPowerEmptyOnDifficultyFormat);
            Add(part, PhraseKind.Solo, FindingMessages.ChrSoloEmptyOnDifficultyFormat);
        }

        return findings;

        void Add(EditablePart part, PhraseKind kind, string format)
        {
            foreach (var phrase in PhraseSpans.PhrasesIn(context, part, kind))
            {
                if (EmptyStarPowerRule.IsEmpty(part, phrase))
                    continue;
                foreach (var track in part.Tracks)
                {
                    if (track.Notes.Count > 0 && PhraseSpans.Covered(track, phrase).IsEmpty)
                        findings.Add(new Finding(RuleId, Severity, string.Format(CultureInfo.CurrentCulture, format, track.Difficulty), Location: PhraseSpans.Span(part, track, phrase)));
                }
            }
        }
    }
}

/// <summary>
/// CHR066: a 5-fret or keys part with notes has no star power phrases, so a player never earns star power on it. One
/// finding for each such part, whose highest difficulty with notes has enough chords for a phrase
/// (<see cref="StarPowerParameters.MinChords"/>). Its fix generates them (<see cref="StarPowerGenerator"/>).
/// </summary>
public sealed class NoStarPowerRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr066;

    public Severity Severity => Severity.Info;

    // Part, not range: adding or removing any phrase, or a difficulty's notes, changes it.
    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            if (!StarPowerGenerator.CanGenerate(part.Kind) || StarPowerGenerator.HasStarPower(part) || DifficultyGenerator.HighestCharted(part) is not { } highest)
                continue;
            if (ChordCount(part[highest]) >= StarPowerParameters.Default.MinChords)
                findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrNoStarPower, Location: new ChartLocation(part.Instrument, null, 0, 0)));
        }

        return findings;
    }

    private static int ChordCount(EditableTrack track)
    {
        var notes = track.Notes.AsSpan();
        int chords = 0;
        for (int i = 0; i < notes.Length; i++)
        {
            if (i == 0 || notes[i].Tick != notes[i - 1].Tick)
                chords++;
        }

        return chords;
    }
}
