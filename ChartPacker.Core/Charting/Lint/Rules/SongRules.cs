using System.Globalization;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Resources;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint.Rules;

/// <summary>
/// CHR080: a part has notes on some difficulties and none on others. One finding for each empty difficulty, at its
/// start.
/// </summary>
public sealed class MissingDifficultyRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr080;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            if (!part.HasNotes)
                continue;
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count == 0)
                {
                    string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrMissingDifficultyFormat, track.Difficulty);
                    findings.Add(new Finding(RuleId, Severity, message, Location: new ChartLocation(part.Instrument, track.Difficulty, 0, 0)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR081: a lower difficulty has more notes per second than the one above it: its chords over the time from its first
/// to its last, against the next difficulty up's. Difficulties with fewer than two chords aren't compared.
/// </summary>
public sealed class LowerDifficultyDenserRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr081;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var map = context.TempoMap;
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            var tracks = part.Tracks;
            for (int i = 0; i + 1 < tracks.Count; i++)
            {
                if (DensityOf(tracks[i], map) is not { } lower || DensityOf(tracks[i + 1], map) is not { } upper || lower <= upper)
                    continue;
                string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrLowerDifficultyDenserFormat, tracks[i].Difficulty, lower, tracks[i + 1].Difficulty, upper);
                var notes = tracks[i].Notes;
                findings.Add(new Finding(RuleId, Severity, message, Location: new ChartLocation(part.Instrument, tracks[i].Difficulty, notes[0].Tick, notes[^1].Tick)));
            }
        }

        return findings;
    }

    /// <summary>The track's chords per second, or null when it has fewer than two, or they're at one time.</summary>
    private static double? DensityOf(EditableTrack track, TempoMap map)
    {
        int chords = 0;
        foreach (var _ in new ChordSpans(track.Notes.AsSpan()))
            chords++;
        if (chords < 2)
            return null;
        double seconds = map.SecondsAt(track.Notes[^1].Tick) - map.SecondsAt(track.Notes[0].Tick);
        return seconds > 0 ? chords / seconds : null;
    }
}

/// <summary>CHR082: the chart has no sections: no <c>[section …]</c> or <c>[prc_…]</c> event.</summary>
public sealed class NoSectionsRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr082;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        foreach (var e in context.Chart.Events.AsSpan())
        {
            if (SectionEvents.IsSection(e.Text))
                return [];
        }

        return [new Finding(RuleId, Severity, FindingMessages.ChrNoSections, Tick: 0)];
    }
}

/// <summary>
/// CHR083: a part with notes has no <c>diff_*</c> value in <c>song.ini</c>, or <c>-1</c>, so the games show it unrated.
/// One finding for each of its keys (§7.5).
/// </summary>
public sealed class UnratedPartRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr083;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Chart.Parts)
        {
            if (!part.HasNotes || !NewChartSettings.Instruments.Contains(part.Instrument))
                continue;
            foreach (string key in SongIniPartKeys.DifficultyKeysOf(part.Instrument, context.Chart.DrumsVariant))
            {
                long? rating = SongIniValues.Integer(SongIniValues.Last(context.SongIni, key));
                if (rating is null or -1)
                {
                    string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrUnratedFormat, key);
                    findings.Add(new Finding(RuleId, Severity, message, Key: key, Location: new ChartLocation(part.Instrument, null, 0, 0)));
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// CHR084: <c>song_length</c> is unset, or more than a second from the audio's length. Nothing is checked while the
/// audio's length isn't known. Chart Builder's save corrects it, so it mostly shows in the library.
/// </summary>
public sealed class SongLengthRule : IChartLintRule
{
    public const string Key = "song_length";

    public string RuleId => ChrRules.Chr084;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (context.AudioLength is not { } length)
            return [];

        long audio = (long)Math.Round(length.TotalMilliseconds);
        long? saved = SongIniValues.Integer(SongIniValues.Last(context.SongIni, Key));
        if (saved is { } ms && Math.Abs(ms - audio) <= PendingSongIniChanges.SongLengthTolerance)
            return [];

        string message = string.Format(CultureInfo.CurrentCulture, FindingMessages.ChrSongLengthFormat, saved?.ToString(CultureInfo.CurrentCulture) ?? "-", audio);
        return [new Finding(RuleId, Severity, message, Key: Key)];
    }
}

/// <summary>CHR085: <c>[end]</c> is missing, or before the last note ends. Saving moves it after (<see cref="ChartSaveFixups.MoveEnd"/>).</summary>
public sealed class EndEventRule : IChartLintRule
{
    public string RuleId => ChrRules.Chr085;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        if (ChartSaveFixups.LastNoteEnd(context.Chart) is not { } last)
            return [];

        long? end = null;
        foreach (var e in context.Chart.Events.AsSpan())
        {
            if (e.Text == ChartSaveFixups.EndText && (end is null || e.Tick > end))
                end = e.Tick;
        }

        return end switch
        {
            null => [new Finding(RuleId, Severity, FindingMessages.ChrEndMissing, Tick: last)],
            { } tick when tick < last => [new Finding(RuleId, Severity, FindingMessages.ChrEndEarly, Tick: tick)],
            _ => [],
        };
    }
}
