using System.Globalization;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>
/// Lint rules that exist only for the engine's tests, one of each scope, so the engine is tested apart from the CHR
/// rules. Their IDs are <c>TST</c>, which no real rule uses.
/// </summary>
internal static class TestLintRules
{
    /// <summary>A rule of each kind that doesn't read the game view.</summary>
    public static IReadOnlyList<IChartLintRule> WithoutGameView =>
        [new CloseRepeatRule(), new LongSustainRule(), new EmptyStarPowerRule(), new MissingDifficultyRule(), new NoSectionsRule()];

    /// <summary>A rule of each kind, and one that reads the game view.</summary>
    public static IReadOnlyList<IChartLintRule> All => [.. WithoutGameView, new GameHopoRule()];

    /// <summary>The range rules, which the incremental timing (AC C16.2) runs.</summary>
    public static IReadOnlyList<IChartLintRule> Range => [new CloseRepeatRule(), new LongSustainRule(), new EmptyStarPowerRule()];

    public static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// TST001, a range rule of reach 1, like the HOPO rules: a chord less than <paramref name="gap"/> ticks after the chord
/// before it that has a lane in common with it. Found at the chord.
/// </summary>
internal sealed class CloseRepeatRule(long gap = 240) : IChartLintRule
{
    public const string Id = "TST001";

    public string RuleId => Id;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 1;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var chord in context.ChordsIn(track))
                {
                    long tick = chord[0].Tick;
                    if (ChartLintContext.PreviousChordTick(track, tick) is not { } previous || tick - previous >= gap)
                        continue;
                    var before = track.Notes.At(previous);
                    foreach (var note in chord)
                    {
                        bool repeats = false;
                        foreach (var other in before)
                            repeats |= other.Lane == note.Lane;
                        if (repeats)
                        {
                            findings.Add(new Finding(RuleId, Severity, TestLintRules.Text($"Lane {note.Lane} again {tick - previous} ticks after {previous}."),
                                Location: new ChartLocation(part.Instrument, track.Difficulty, tick, tick)));
                            break;
                        }
                    }
                }
            }
        }

        return findings;
    }
}

/// <summary>TST002, a range rule of reach 0: a note held longer than <paramref name="limit"/> ticks. Found at the note's start.</summary>
internal sealed class LongSustainRule(long limit = 960) : IChartLintRule
{
    public const string Id = "TST002";

    public string RuleId => Id;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var note in context.NotesIn(track))
                {
                    if (note.Length > limit)
                    {
                        findings.Add(new Finding(RuleId, Severity, TestLintRules.Text($"Lane {note.Lane} held {note.Length} ticks."),
                            Location: new ChartLocation(part.Instrument, track.Difficulty, note.Tick, note.Tick)));
                    }
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// TST003, a range rule of reach 0 about what every difficulty shares: a star-power phrase with no Expert note starting
/// in it. Found over the phrase, on every difficulty.
/// </summary>
internal sealed class EmptyStarPowerRule : IChartLintRule
{
    public const string Id = "TST003";

    public string RuleId => Id;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Range;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var phrase in context.PhrasesIn(part))
            {
                long end = phrase.Tick + phrase.Length;
                if (phrase.Kind == PhraseKind.StarPower && part[ChartDifficulty.Expert].Notes.Between(phrase.Tick, end).IsEmpty)
                    findings.Add(new Finding(RuleId, Severity, "Star power with no notes.", Location: new ChartLocation(part.Instrument, null, phrase.Tick, end)));
            }
        }

        return findings;
    }
}

/// <summary>TST004, a part rule: a part with notes on some difficulties and none on others. Found at tick 0 of each empty one.</summary>
internal sealed class MissingDifficultyRule : IChartLintRule
{
    public const string Id = "TST004";

    public string RuleId => Id;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            if (part.Tracks.All(t => t.Notes.Count == 0))
                continue;
            foreach (var track in part.Tracks.Where(t => t.Notes.Count == 0))
                findings.Add(new Finding(RuleId, Severity, "No notes.", Location: new ChartLocation(part.Instrument, track.Difficulty, 0, 0)));
        }

        return findings;
    }
}

/// <summary>TST005, a song rule: no <c>[section]</c> events. Found without a location, as it's about the song.</summary>
internal sealed class NoSectionsRule : IChartLintRule
{
    public const string Id = "TST005";

    public string RuleId => Id;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Song;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        bool any = context.Chart.Events.Any(e => e.Text.StartsWith("[section", StringComparison.Ordinal));
        return any ? [] : [new Finding(RuleId, Severity, "No sections.")];
    }
}

/// <summary>
/// TST006, a part rule that reads the game view: each difficulty the games read HOPOs on, natural or forced. Found over
/// its HOPOs.
/// </summary>
internal sealed class GameHopoRule : IChartLintRule
{
    public const string Id = "TST006";

    public string RuleId => Id;

    public Severity Severity => Severity.Info;

    public LintScope Scope => LintScope.Part;

    public int Reach => 0;

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        var findings = new List<Finding>();
        foreach (var part in context.Parts)
        {
            foreach (var track in context.TracksOf(part))
            {
                if (!context.GameView.Tracks.TryGetValue(new ChartTrackKey(part.Instrument, track.Difficulty), out var notes))
                    continue;
                var hopos = notes.Notes.Where(n => (n.Flags & NoteFlags.Hopo) != 0).ToList();
                if (hopos.Count > 0)
                {
                    findings.Add(new Finding(RuleId, Severity, TestLintRules.Text($"{hopos.Count} HOPOs."),
                        Location: new ChartLocation(part.Instrument, track.Difficulty, hopos[0].Tick, hopos[^1].Tick)));
                }
            }
        }

        return findings;
    }
}

/// <summary>A rule that runs another and keeps what it was given each time: the part and region, and the chords in scope.</summary>
internal sealed class RecordingRule(IChartLintRule inner) : IChartLintRule
{
    public string RuleId => inner.RuleId;

    public Severity Severity => inner.Severity;

    public LintScope Scope => inner.Scope;

    public int Reach => inner.Reach;

    /// <summary>Each run's part and region, in order.</summary>
    public List<(ChartInstrument? Part, ChartLocation? Region)> Runs { get; } = [];

    /// <summary>Each chord the runs had in scope, as part, difficulty and tick.</summary>
    public List<(ChartInstrument Part, ChartDifficulty Difficulty, long Tick)> Chords { get; } = [];

    public IEnumerable<Finding> Check(ChartLintContext context)
    {
        Runs.Add((context.Part, context.Region));
        foreach (var part in context.Parts)
        {
            foreach (var track in context.TracksOf(part))
            {
                foreach (var chord in context.ChordsIn(track))
                    Chords.Add((part.Instrument, track.Difficulty, chord[0].Tick));
            }
        }

        return inner.Check(context);
    }
}

/// <summary>A rule given by its scope and a function, for one test's needs.</summary>
internal sealed class DelegateRule(string id, LintScope scope, Func<ChartLintContext, IEnumerable<Finding>> check, int reach = 0) : IChartLintRule
{
    public string RuleId => id;

    public Severity Severity => Severity.Warning;

    public LintScope Scope => scope;

    public int Reach => reach;

    public IEnumerable<Finding> Check(ChartLintContext context) => check(context);
}
