using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>Charts for the lint tests, and checks on lint results.</summary>
internal static class LintCharts
{
    private const int Snap = 60;

    /// <summary>
    /// A random chart of guitar, bass and pro drums: up to <paramref name="notes"/> notes on each difficulty, some
    /// difficulties left empty, some long sustains, and a few star-power phrases and sections, over the ticks
    /// <see cref="CommandSequenceTests.RandomCommands"/> edits.
    /// </summary>
    public static EditableChart Generated(Random random, int notes)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums }, DrumsVariant.Pro));
        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                if (random.Next(4) == 0)
                    continue;
                for (int i = 0; i < notes; i++)
                {
                    long tick = random.Next(256) * (long)Snap;
                    if (part.Kind == ChartInstrumentKind.Drums)
                        track.Notes.Add(new EditableNote(tick, 1, random.Next(0, 5)));
                    else
                        track.Notes.Add(new EditableNote(tick, random.Next(6) == 0 ? random.Next(1, 40) * Snap : 1, random.Next(-1, 5)));
                }
            }

            for (int i = random.Next(4); i > 0; i--)
                part.Phrases.Add(new Phrase(PhraseKind.StarPower, random.Next(256) * (long)Snap, random.Next(1, 16) * Snap));
        }

        if (random.Next(2) == 0)
            chart.Events.Add(TextEvent.Of(random.Next(256) * (long)Snap, "[section Intro]"));
        return chart;
    }

    /// <summary>
    /// A guitar part whose Expert notes are a chord every <paramref name="spacing"/> ticks from 0, <paramref name="count"/>
    /// of them, each in the lane after the last's; and whose Hard notes are every <paramref name="hardSpacing"/> ticks over
    /// the same ticks.
    /// </summary>
    public static EditableChart Ladder(int count, long spacing = 480, long hardSpacing = 960)
    {
        var chart = MidiChartWriterTests.NewChart(ChartInstrument.Guitar);
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        for (int i = 0; i < count; i++)
            guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(i * spacing, 1, i % 5));
        for (long tick = 0; tick < count * spacing; tick += hardSpacing)
            guitar[ChartDifficulty.Hard].Notes.Add(new EditableNote(tick, 1, 0));
        return chart;
    }

    /// <summary>
    /// A chart for AC C16.2: 20,000 guitar notes on Expert, a chord every 120 ticks in random lanes with now and then a
    /// long sustain, and star power every 32 beats; and 2,000 notes each on guitar Hard, bass Expert and drums Expert.
    /// </summary>
    public static EditableChart Big()
    {
        var random = new Random(16);
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums }, DrumsVariant.Pro));
        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        for (int i = 0; i < 20_000; i++)
            guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(i * 120L, i % 50 == 0 ? 1_200 : 1, random.Next(0, 5)));
        for (int i = 0; i < 2_000; i++)
        {
            guitar[ChartDifficulty.Hard].Notes.Add(new EditableNote(i * 1_200L, 1, random.Next(0, 5)));
            chart.PartOf(ChartInstrument.Bass)![ChartDifficulty.Expert].Notes.Add(new EditableNote(i * 1_200L, 1, random.Next(0, 5)));
            chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.Add(new EditableNote(i * 1_200L, 1, random.Next(0, 5)));
        }

        for (long tick = 0; tick < 20_000 * 120L; tick += 32 * 480)
            guitar.Phrases.Add(new Phrase(PhraseKind.StarPower, tick, 4 * 480));
        return chart;
    }

    /// <summary>What lint is given: a snapshot of <paramref name="chart"/>, taken now.</summary>
    public static ChartLintInput Input(EditableChart chart, ChartReadOptions? options = null) => new(chart.Clone(), options: options);

    /// <summary>Fails unless the two results have the same findings, in the same order, rule by rule.</summary>
    public static void AssertSameFindings(ChartLintResult expected, ChartLintResult actual, string when)
    {
        foreach (string rule in expected.RuleIds.Order(StringComparer.Ordinal))
        {
            var want = expected.FindingsOf(rule);
            var got = actual.FindingsOf(rule);
            if (want.SequenceEqual(got))
                continue;
            var missing = want.Except(got).Take(3).Select(Describe);
            var extra = got.Except(want).Take(3).Select(Describe);
            Assert.Fail($"{rule} {when}: {want.Count} findings expected, {got.Count} found. Missing: {string.Join("; ", missing)}. Extra: {string.Join("; ", extra)}.");
        }

        Assert.AreSequenceEqual(expected.Findings.ToList(), [.. actual.Findings], when);
    }

    public static string Describe(Finding finding) => finding.Location is { } l
        ? $"{finding.RuleId} {l.Part} {l.Difficulty?.ToString() ?? "all"} {l.Tick}-{l.EndTick} \"{finding.Message}\""
        : $"{finding.RuleId} \"{finding.Message}\"";
}
