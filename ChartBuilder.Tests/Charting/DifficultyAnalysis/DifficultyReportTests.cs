using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Charting.DifficultyAnalysis.DifficultyCharts;

namespace ChartBuilder.Tests.Charting.DifficultyAnalysis;

/// <summary>The difficulty report: every song under a folder, its parts' song.ini ratings beside the model's, as CSV.</summary>
[TestClass]
public sealed class DifficultyReportTests
{
    private string _root = "";

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartbuilder-report-").FullName;

    [TestCleanup]
    public void DeleteRoot() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public async Task EachPartHasItsReportedRatingBesideTheModels()
    {
        var chart = Chart(Varied(1, 16, 480, [0.25, 0.5]), bpm: 160);
        new AddPart(ChartInstrument.Bass).Apply(chart);
        new AddNotes(ChartInstrument.Bass, Expert, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 480L, 1, i % 2))]).Apply(chart);
        string song = Song(Path.Combine("Artist", "Song"), chart, "diff_guitar = 4", "diff_bass = -1");

        var rows = await DifficultyReport.BuildAsync(_root, 10, 2, cancellationToken: TestContext.CancellationToken);

        var row = rows.Single();
        Assert.AreEqual(song, row.SongPath);
        Assert.IsNull(row.Problem);
        var guitar = row.Parts.Single(p => p.Instrument == ChartInstrument.Guitar);
        var bass = row.Parts.Single(p => p.Instrument == ChartInstrument.Bass);
        var rhythm = row.Parts.Single(p => p.Instrument == ChartInstrument.Rhythm);
        Assert.AreEqual(4, guitar.Reported);
        Assert.AreEqual(-1, bass.Reported);
        Assert.IsNull(rhythm.Reported);
        Assert.IsNotNull(guitar.Analyzed);
        Assert.IsNotNull(bass.Analyzed);
        Assert.IsNull(rhythm.Analyzed);
        Assert.IsGreaterThan(bass.Analyzed.Rank, guitar.Analyzed.Rank);
        Assert.AreEqual(DifficultyEstimator.EstimatePart(chart, ChartInstrument.Guitar, Options)!.Rating, guitar.Analyzed);
    }

    [TestMethod]
    public async Task ProDrumsAreReportedForDiffDrumsAndDiffDrumsReal()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 140, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Drums, Expert, [.. Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, i % 4))]).Apply(chart);
        Song("Drums", chart, "diff_drums = 3", "diff_drums_real = 4", "pro_drums = True");

        var rows = await DifficultyReport.BuildAsync(_root, 10, 2, cancellationToken: TestContext.CancellationToken);

        var drums = rows.Single().Parts.Where(p => p.Instrument == ChartInstrument.Drums).ToList();
        Assert.AreSequenceEqual(["diff_drums", "diff_drums_real"], drums.Select(p => p.Key).ToList());
        Assert.AreSequenceEqual([3, 4], drums.Select(p => p.Reported ?? -9).ToList());
        Assert.AreEqual(DifficultyInstrument.Drums, drums[0].Analyzed!.Instrument);
        Assert.AreEqual(DifficultyInstrument.ProDrums, drums[1].Analyzed!.Instrument);
    }

    [TestMethod]
    public void TheCsvHasAColumnForEachValueOfEachPartToATenth()
    {
        var rating = new DifficultyRating(5.4321, new DifficultyAttributes(12.25, 0.04, 99.95, 33.3333, 7));
        var row = new DifficultyReportRow(
            @"C:\Songs\Artist, The - Song",
            [new DifficultyReportPart(ChartInstrument.Guitar, "diff_guitar", 5, rating), new DifficultyReportPart(ChartInstrument.Bass, "diff_bass", null, null)],
            null);

        var lines = Csv([row]);

        Assert.HasCount(2, lines);
        var header = lines[0].Split(',');
        Assert.HasCount(1 + (DifficultyReport.Columns.Count * 7) + 1, header);
        Assert.AreSequenceEqual(
            ["Song", "Guitar reported", "Guitar analyzed", "Guitar strum", "Guitar note", "Guitar rhythm", "Guitar pattern", "Guitar endurance", "Bass reported"],
            [.. header.Take(9)]);
        Assert.AreEqual("Problem", header[^1]);
        Assert.Contains("Drums kick", header);
        Assert.Contains("Pro Drums hands", header);
        Assert.Contains("GHL Guitar strum", header);

        // The path has a comma, so it's quoted; the bass and the parts after it are empty.
        string expected = "\"C:\\Songs\\Artist, The - Song\",5,5.4,12.3,0.0,100.0,33.3,7.0" + string.Concat(Enumerable.Repeat(",", ((DifficultyReport.Columns.Count - 1) * 7) + 1));
        Assert.AreEqual(expected, lines[1]);
    }

    [TestMethod]
    public async Task ASongThatCantBeReadSaysWhyAndTheRestAreStillRated()
    {
        Song("Good", Chart(Varied(2, 8, 480, [0.5])), "diff_guitar = 2");
        string broken = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(broken);
        File.WriteAllBytes(Path.Combine(broken, "song.ini"), Encoding.UTF8.GetBytes("[song]\nname = Broken"));
        File.WriteAllBytes(Path.Combine(broken, "notes.mid"), "not a midi file"u8.ToArray());

        var rows = await DifficultyReport.BuildAsync(_root, 10, 2, cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(2, rows);
        var bad = rows.Single(r => r.SongPath == broken);
        Assert.IsNotNull(bad.Problem);
        Assert.IsEmpty(bad.Parts);
        Assert.IsNull(rows.Single(r => r.SongPath != broken).Problem);
        Assert.DoesNotEndWith(",", Csv(rows).Single(l => l.StartsWith(broken, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OnlySongsWithinTheDepthAreRated()
    {
        Song("Top", Chart(Varied(3, 4, 480, [0.5])));
        Song(Path.Combine("A", "B", "Deep"), Chart(Varied(4, 4, 480, [0.5])));

        var shallow = await DifficultyReport.BuildAsync(_root, 1, 2, cancellationToken: TestContext.CancellationToken);
        var deep = await DifficultyReport.BuildAsync(_root, 3, 2, cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(1, shallow);
        Assert.HasCount(2, deep);
    }

    private string Song(string name, EditableChart chart, params string[] ini)
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "song.ini"), Encoding.UTF8.GetBytes(string.Join("\n", ["[song]", "name = Song", .. ini])));
        File.WriteAllBytes(Path.Combine(folder, "notes.mid"), MidiChartWriter.Write(chart));
        return folder;
    }

    private static string[] Csv(IEnumerable<DifficultyReportRow> rows)
    {
        using var writer = new StringWriter();
        DifficultyReport.WriteCsv(writer, rows);
        return writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }
}
