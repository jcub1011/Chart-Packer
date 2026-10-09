using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint.Rules;

/// <summary>
/// What the vocal rules share. They're song rules, as a vocal part isn't one of the model's parts: each finding is at a
/// tick of a vocal track, and names the track (<see cref="Finding.Track"/>).
/// </summary>
internal static class VocalFindings
{
    /// <summary>A finding of <paramref name="rule"/> at <paramref name="tick"/> of <paramref name="track"/>.</summary>
    public static Finding At(IChartLintRule rule, VocalTrack track, long? tick, string message) =>
        new(rule.RuleId, rule.Severity, message, Tick: tick, Track: VocalTracks.NameOf(track));

    /// <summary>The findings of <paramref name="rule"/> for every vocal part: each note <paramref name="isFaulty"/> picks.</summary>
    public static List<Finding> EachNote(
        IChartLintRule rule, ChartLintContext context, string message, Func<VocalTrackContent, int, bool> isFaulty)
    {
        var findings = new List<Finding>();
        foreach (var (track, content) in context.VocalParts)
        {
            for (int i = 0; i < content.Notes.Count; i++)
            {
                if (isFaulty(content, i))
                    findings.Add(At(rule, track, content.Notes[i].Tick, message));
            }
        }

        return findings;
    }

    /// <summary>The ticks <paramref name="content"/>'s lyrics are on.</summary>
    public static HashSet<long> LyricTicks(VocalTrackContent content) => [.. content.Lyrics.Select(static l => l.Tick)];

    /// <summary>The index of the phrase of <paramref name="content"/> that <paramref name="tick"/> is inside, or -1.</summary>
    public static int PhraseAt(VocalTrackContent content, long tick)
    {
        for (int i = 0; i < content.Phrases.Count; i++)
        {
            var phrase = content.Phrases[i];
            if (phrase.Tick > tick)
                break;
            if (tick < phrase.EndTick)
                return i;
        }

        return -1;
    }
}

/// <summary>CHR090: a vocal note runs into the next one, as a part sings one note at a time.</summary>
public sealed class VocalNotesOverlapRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr090;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context) =>
        VocalFindings.EachNote(this, context, FindingMessages.ChrVocalNotesOverlap, static (content, i) =>
        {
            var notes = content.Notes;
            var note = notes[i];
            for (int next = i + 1; next < notes.Count && notes[next].Tick < note.EndTick; next++)
            {
                if (notes[next].Tick > note.Tick || notes[next].Pitch != note.Pitch)
                    return true;
            }

            return false;
        });
}

/// <summary>CHR091: a vocal note has no lyric. A slide's is <c>+</c>.</summary>
public sealed class VocalNoteWithoutLyricRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr091;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var (track, content) in context.VocalParts)
        {
            var lyrics = VocalFindings.LyricTicks(content);
            foreach (var note in content.Notes)
            {
                if (!lyrics.Contains(note.Tick))
                    findings.Add(VocalFindings.At(this, track, note.Tick, FindingMessages.ChrVocalNoteWithoutLyric));
            }
        }

        return findings;
    }
}

/// <summary>CHR092: a vocal note is outside every phrase, so the games don't show it.</summary>
public sealed class VocalNoteOutsidePhraseRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr092;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context) =>
        VocalFindings.EachNote(this, context, FindingMessages.ChrVocalNoteOutsidePhrase, static (content, i) => VocalFindings.PhraseAt(content, content.Notes[i].Tick) < 0);
}

/// <summary>CHR093: a lyric isn't on any vocal note's start, so the games show it without a note to sing.</summary>
public sealed class LyricWithoutNoteRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr093;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var (track, content) in context.VocalParts)
        {
            var starts = content.Notes.Select(static n => n.Tick).ToHashSet();
            foreach (long tick in content.Lyrics.Select(static l => l.Tick).Distinct())
            {
                if (!starts.Contains(tick))
                    findings.Add(VocalFindings.At(this, track, tick, FindingMessages.ChrLyricWithoutNote));
            }
        }

        return findings;
    }
}

/// <summary>CHR094: a slide (<c>+</c>) has no sung note before it in its phrase to carry on from.</summary>
public sealed class SlideWithoutNoteRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr094;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context) =>
        VocalFindings.EachNote(this, context, FindingMessages.ChrSlideWithoutNote, static (content, i) =>
        {
            var note = content.Notes[i];
            if (!VocalEdits.IsSlide(content, note.Tick))
                return false;

            // The note before, on an earlier tick.
            int before = i - 1;
            while (before >= 0 && content.Notes[before].Tick == note.Tick)
                before--;
            if (before < 0)
                return true;
            var previous = content.Notes[before];
            int phrase = VocalFindings.PhraseAt(content, note.Tick);
            return phrase < 0 || VocalFindings.PhraseAt(content, previous.Tick) != phrase || VocalEdits.IsSpoken(content, previous.Tick);
        });
}

/// <summary>CHR095: the chart has <c>HARM2</c> or <c>HARM3</c> without <c>HARM1</c>, which the games need for harmonies.</summary>
public sealed class HarmonyWithoutFirstRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr095;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var chart = context.Chart;
        if (VocalTracks.IndexIn(chart, VocalTrack.Harmony1) >= 0)
            return [];
        return ((VocalTrack[])[VocalTrack.Harmony2, VocalTrack.Harmony3])
            .Where(t => VocalTracks.IndexIn(chart, t) >= 0)
            .Select(t => VocalFindings.At(this, t, null, FindingMessages.ChrHarmonyWithoutFirst))
            .ToList();
    }
}
