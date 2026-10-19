using ChartShared.Audio.Synthesis;
using ChartShared.Core.Calibration;
using ChartShared.Core.Charts;

namespace ChartBuilder.Tests.Calibration;

[TestClass]
public sealed class CalibrationChartTests
{
    private const int Rate = 48000;

    [TestMethod]
    public void ItIs80BpmIn44()
    {
        var tempo = CalibrationChart.Tempo;

        Assert.AreEqual(0.75, CalibrationChart.BeatSeconds);
        Assert.AreEqual(0.75, tempo.SecondsAt(CalibrationChart.Resolution), 1e-12);
        Assert.AreSequenceEqual(
            new[] { true, false, false, false, true, false, false, false },
            [.. tempo.BeatLines(0, (CalibrationChart.Resolution * 8L) - 1).Select(b => b.IsMeasure)]);
    }

    [TestMethod]
    public void EachNoteIsAnOpenNoteOnTheNextBeat()
    {
        var notes = CalibrationChart.Notes(3, first: 8);

        Assert.AreSequenceEqual(
            new[] { 8L * CalibrationChart.Resolution, 9L * CalibrationChart.Resolution, 10L * CalibrationChart.Resolution },
            [.. notes.Select(n => n.Tick)]);
        Assert.IsTrue(notes.All(n => n.Lane == ChartNote.OpenLane && n.Length == 0 && n.Flags == NoteFlags.None));
    }

    [TestMethod]
    [DataRow(-1.0, 0)]
    [DataRow(0.0, 1)]
    [DataRow(0.74, 1)]
    [DataRow(0.75, 2)]
    [DataRow(3.0, 5)]
    public void BeatsInCountsTheFirstBeat(double seconds, int beats) =>
        Assert.AreEqual(beats, CalibrationChart.BeatsIn(TimeSpan.FromSeconds(seconds)));

    [TestMethod]
    public void TheMetronomeClicksAsEachNoteIsStruck()
    {
        // As the tool plays it: a bar's lead, then a note on each beat, the measures' clicks accented.
        var lead = TimeSpan.FromSeconds(2);
        var length = TimeSpan.FromSeconds(10);
        var notes = CalibrationChart.Notes(CalibrationChart.BeatsIn(length - lead));
        var clicks = new MetronomeSchedule(CalibrationChart.Tempo, lead, length, Rate);

        Assert.AreEqual(notes.Length, clicks.Count);
        for (int i = 0; i < notes.Length; i++)
        {
            double struck = CalibrationChart.Tempo.SecondsAt(notes[i].Tick) + lead.TotalSeconds;
            Assert.AreEqual((long)Math.Round(struck * Rate), clicks.FrameAt(i));
            Assert.AreEqual(i % 4 == 0, clicks.IsMeasureAt(i));
        }
    }
}
