using System.Globalization;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Resources;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Charting.Lint.Rules;

/// <summary>CHR001: no time signature at tick 0, so the games assume 4/4 (charter-spec §6).</summary>
public sealed class NoTimeSignatureAtStartRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr001;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (context.Chart.TempoMap.TimeSignatures.At(0).IsEmpty)
            yield return new Finding(RuleId, Severity, FindingMessages.ChrNoTimeSignatureAtStart, Tick: 0);
    }
}

/// <summary>CHR002: a tempo below 20 or above 400 BPM, probably a typo or a half- or double-time mistake.</summary>
public sealed class TempoOutOfRangeRule : IChartLintRule
{
    /// <summary>The slowest tempo that isn't flagged.</summary>
    public const double MinBpm = 20;

    /// <summary>The fastest tempo that isn't flagged.</summary>
    public const double MaxBpm = 400;

    public string RuleId => ChrRules.Chr002;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var tempo in context.Chart.TempoMap.Tempos.AsSpan())
        {
            double bpm = EditableTempoMap.BpmOf(tempo.MicrosecondsPerQuarter);
            if (bpm < MinBpm || bpm > MaxBpm)
            {
                string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrTempoOutOfRangeFormat, bpm.ToString("0.###", CultureInfo.CurrentCulture));
                findings.Add(new Finding(RuleId, Severity, message, Tick: tempo.Tick));
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR003: a note, phrase or event after the end of the audio, in audio time (§4.4.1): one finding for each track's
/// notes past it, one for each part's phrases, and one for each event. Nothing is checked while the audio's length isn't
/// known. <c>[music_end]</c> and <c>[end]</c> aren't events the players see, so they may be after the audio:
/// <c>[music_end]</c> is placed at the audio's last sound, rounded up to a tick.
/// </summary>
public sealed class AfterTheAudioRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr003;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (context.AudioLength is not { } length)
            return [];

        var map = context.TempoMap;
        double end = length.TotalSeconds - context.Delay.TotalSeconds;
        long firstPast = Math.Max(0, (long)Math.Floor(map.TickAt(end)) + 1);
        while (firstPast > 0 && map.SecondsAt(firstPast - 1) > end)
            firstPast--;

        var findings = new List<Finding>();
        foreach (var part in context.Chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                var past = track.Notes.Between(firstPast, long.MaxValue);
                if (past.IsEmpty)
                    continue;
                string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrNotesAfterAudioFormat, past.Length);
                findings.Add(new Finding(RuleId, Severity, message, Location: new ChartLocation(part.Instrument, track.Difficulty, past[0].Tick, past[^1].Tick)));
            }

            var phrases = part.Phrases.AsSpan()[part.Phrases.FirstAtOrAfter(firstPast)..];
            if (!phrases.IsEmpty)
            {
                string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrPhrasesAfterAudioFormat, phrases.Length);
                findings.Add(new Finding(RuleId, Severity, message, Location: new ChartLocation(part.Instrument, null, phrases[0].Tick, phrases[^1].Tick)));
            }
        }

        foreach (var e in context.Chart.Events.Between(firstPast, long.MaxValue))
        {
            if (SongEvents.KindOf(e.Text) is not (SongEvent.MusicEnd or SongEvent.End))
                findings.Add(new Finding(RuleId, Severity, string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrEventAfterAudioFormat, e.Text), Tick: e.Tick));
        }

        return findings;
    }
}

/// <summary>
/// CHR004: a note isn't on any common grid: a whole-note division from 1/1 to 1/192, or a triplet one, counted from the
/// time signature in force as the editor's snap is. It never uses the editor's current snap, so it gives the same result
/// in the library (C16 item 5). One finding for each chord.
/// </summary>
public sealed class OffGridRule : IChartLintRule
{
    // Every common grid is a subdivision of one of these two (SnapGrid.CommonDivisions): 1/128's halvings and 1/192's.
    private static readonly SnapGrid[] FinestGrids = [SnapGrid.Of(128), SnapGrid.Of(192)];

    public string RuleId => ChrRules.Chr004;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    /// <summary>Whether <paramref name="tick"/> is on a common grid of <paramref name="map"/>.</summary>
    public static bool IsOnGrid(long tick, TempoMap map)
    {
        // A loop rather than Any, whose lambda would capture both arguments for every chord.
        foreach (var grid in FinestGrids)
        {
            if (grid.Nearest(tick, map) == tick)
                return true;
        }

        return false;
    }

    /// <summary>The ticks on a common grid either side of <paramref name="tick"/>, nearest it: the one before and the one after.</summary>
    public static (long Before, long After) GridLinesAround(long tick, TempoMap map) =>
        (FinestGrids.Max(g => g.Step(tick, -1, map)), FinestGrids.Min(g => g.Step(tick, 1, map)));

    /// <summary>The nearest tick to <paramref name="tick"/> on a common grid, the coarser grid's when two are as near.</summary>
    public static long NearestOnGrid(long tick, TempoMap map)
    {
        long best = FinestGrids[0].Nearest(tick, map);
        foreach (var grid in FinestGrids.AsSpan(1))
        {
            long candidate = grid.Nearest(tick, map);
            if (Math.Abs(candidate - tick) < Math.Abs(best - tick))
                best = candidate;
        }

        return best;
    }

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var map = context.TempoMap;
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var chord in context.ChordsIn(track))
                {
                    long tick = chord[0].Tick;
                    if (!IsOnGrid(tick, map))
                        findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrOffGrid, Location: new ChartLocation(part.Instrument, track.Difficulty, tick, tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>CHR005: two tempo markers, or two time signatures, on the same tick. The games read only the last.</summary>
public sealed class DuplicateMarkersRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr005;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var map = context.Chart.TempoMap;
        var findings = new List<Finding>();
        foreach (long tick in DuplicateTicks(map.Tempos.AsSpan().ToArray().Select(t => t.Tick)))
            findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrDuplicateTempo, Tick: tick));
        foreach (long tick in DuplicateTicks(map.TimeSignatures.AsSpan().ToArray().Select(t => t.Tick)))
            findings.Add(new Finding(RuleId, Severity, FindingMessages.ChrDuplicateTimeSignature, Tick: tick));
        return findings;
    }

    private static IEnumerable<long> DuplicateTicks(IEnumerable<long> ticks) =>
        ticks.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key);
}
