using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Tests;

[TestClass]
public sealed class ChartFilesTests
{
    [TestMethod]
    [DataRow("notes.chart", true)]
    [DataRow("NOTES.Chart", true)]
    [DataRow("notes.mid", true)]
    [DataRow("Notes.MID", true)]
    [DataRow("notes.midi", false)]
    [DataRow("song.chart", false)]
    [DataRow("charts/notes.chart", false)]
    [DataRow("notes.chart.bak", false)]
    public void IsChartNameMatchesTheStoredNamesOnly(string name, bool expected) =>
        Assert.AreEqual(expected, ChartFiles.IsChartName(name));

    [TestMethod]
    [DataRow(@"C:\charts\Expert+.chart", "notes.chart")]
    [DataRow(@"C:\charts\song.CHART", "notes.chart")]
    [DataRow(@"C:\charts\song.mid", "notes.mid")]
    [DataRow(@"C:\charts\song.MIDI", "notes.mid")]
    [DataRow(@"C:\charts\song.txt", null)]
    [DataRow(@"C:\charts\notes", null)]
    public void StoredNameForGoesByTheExtension(string source, string? expected) =>
        Assert.AreEqual(expected, ChartFiles.StoredNameFor(source));

    [TestMethod]
    public void FilesOfFindsBothFormats()
    {
        var files = ChartFiles.FilesOf(["song.ogg", "Notes.mid", "album.png", "notes.chart", "old/notes.chart"]);

        Assert.AreSequenceEqual(["Notes.mid", "notes.chart"], [.. files]);
    }

    [TestMethod]
    public void EveryStoredNameIsInTheFilenameRegistry()
    {
        Assert.IsTrue(FilenameRules.IsRegistered(ChartFiles.ChartName));
        Assert.IsTrue(FilenameRules.IsRegistered(ChartFiles.MidiName));
    }
}
