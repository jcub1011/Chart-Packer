using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>Guitar charts to rate, and their difficulty as the games read them.</summary>
internal static class DifficultyCharts
{
    public const ChartInstrument Guitar = ChartInstrument.Guitar;
    public const ChartDifficulty Expert = ChartDifficulty.Expert;
    public static readonly ChartReadOptions Options = new();

    /// <summary>A guitar chart in 4/4 at <paramref name="bpm"/> with <paramref name="notes"/> on Expert.</summary>
    public static EditableChart Chart(IEnumerable<EditableNote> notes, int bpm = 120, int resolution = 480)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", resolution, bpm, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
        var list = notes.ToList();
        if (list.Count > 0)
            new AddNotes(Guitar, Expert, list).Apply(chart);
        return chart;
    }

    /// <summary>The note events of <paramref name="chart"/>'s Expert guitar, as the games read it.</summary>
    public static IReadOnlyList<DifficultyNote> Notes(EditableChart chart) =>
        DifficultyNotes.Of(GameView.Project(chart, new ChartTrackKey(Guitar, Expert), Options), chart.TempoMap.ToTempoMap(), DifficultyNotes.EndTickOf(chart));

    /// <summary>The difficulty of <paramref name="chart"/>'s Expert guitar.</summary>
    public static DifficultyProfile Profile(EditableChart chart) => DifficultyAnalyzer.Analyze(Notes(chart));

    /// <summary>
    /// A made-up chart of <paramref name="phrases"/> phrases of sixteen notes, each phrase one note value of
    /// <paramref name="steps"/> (in beats), with single notes, chords and opens picked at random from <paramref name="seed"/>.
    /// </summary>
    public static List<EditableNote> Varied(int seed, int phrases, int resolution, IReadOnlyList<double> steps, long startTick = 0)
    {
        var random = new Random(seed);
        var notes = new List<EditableNote>();
        double tick = startTick;
        for (int phrase = 0; phrase < phrases; phrase++)
        {
            double step = steps[random.Next(steps.Count)] * resolution;
            for (int i = 0; i < 16; i++)
            {
                long at = (long)Math.Round(tick);
                int roll = random.Next(10);
                if (roll == 0)
                    notes.Add(new EditableNote(at, 1, ChartNote.OpenLane));
                else
                {
                    int lane = random.Next(5);
                    notes.Add(new EditableNote(at, 1, lane));
                    if (roll >= 8 && lane < 4)
                        notes.Add(new EditableNote(at, 1, lane + 1 + random.Next(4 - lane)));
                }

                tick += step;
            }
        }

        return notes;
    }

    /// <summary><paramref name="notes"/> with every tick multiplied by <paramref name="factor"/>.</summary>
    public static IEnumerable<EditableNote> Scaled(IEnumerable<EditableNote> notes, long factor) => notes.Select(n => n with { Tick = n.Tick * factor });
}
