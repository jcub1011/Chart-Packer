using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class NaturalHopoDisplayTests
{
    /// <summary>Each HOPO-threshold setting song.ini can have, and none.</summary>
    public static IEnumerable<object[]> Settings =>
    [
        [""],
        ["hopo_frequency=170"],
        ["hopo_frequency=90"],
        ["eighthnote_hopo=1"],
        ["eighthnote_hopo=0"],
        ["hopofreq=0"],
        ["hopofreq=1"],
        ["hopofreq=2"],
        ["hopofreq=3"],
        ["hopofreq=4"],
        ["hopofreq=5"],
    ];

    [TestMethod]
    [DynamicData(nameof(Settings))]
    public void TheNaturalHoposShownMatchTheGamesReadingAfterSaving(string setting)
    {
        // AC C5.2: the editor draws from the session's game view, which is checked here against ChartReader.
        var ini = setting.Length == 0 ? [] : new[] { new KeyValuePair<string, string>(setting.Split('=')[0], setting.Split('=')[1]) };
        var chart = Guitar();
        var track = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];

        // Gaps either side of every threshold the settings give, alternating lanes, with a repeated lane and a chord.
        long tick = 0;
        int lane = 0;
        foreach (long gap in new long[] { 60, 80, 81, 90, 91, 96, 120, 121, 160, 161, 162, 170, 171, 240, 241, 320, 480, 80, 80 })
        {
            tick += gap;
            lane = (lane + 1) % 5;
            track.Notes.Add(new EditableNote(tick, 1, lane));
        }

        track.Notes.Add(new EditableNote(tick + 100, 1, lane));
        track.Notes.Add(new EditableNote(tick + 200, 1, 0));
        track.Notes.Add(new EditableNote(tick + 200, 1, 1));

        var session = Of(chart, songIni: ini);
        var shown = session.GameTrack(GuitarExpert).Notes.Select(n => (n.Tick, n.Lane, Hopo: n.Flags.HasFlag(NoteFlags.Hopo))).ToList();
        var read = ChartReader.Read(MidiChartWriter.Write(chart), "notes.mid", session.ReadOptions).Tracks[GuitarExpert].Notes
            .Select(n => (n.Tick, n.Lane, Hopo: n.Flags.HasFlag(NoteFlags.Hopo))).ToList();

        Assert.AreSequenceEqual(read, shown);
        Assert.IsTrue(shown.Any(n => n.Hopo) && shown.Any(n => !n.Hopo), "The chart has both kinds.");
    }
}
