using ChartPacker.Core.Charting;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartStemsTests
{
    [TestMethod]
    public void NamedFilesGetTheirStemsAndAnUnnamedOneIsTheSong()
    {
        // AC C1.4, first half.
        var stems = NewChartStems.Assign([], [@"C:\in\Guitar.OGG", @"C:\in\Drums.ogg", @"C:\in\mix.mp3"]);

        Assert.AreSequenceEqual(["guitar", "drums", "song"], stems);
        Assert.IsEmpty(NewChartStems.Problems(stems));
    }

    [TestMethod]
    public void OnlyTheFirstUnnamedFileIsTheSong()
    {
        // AC C1.4, second half: one is the song, the other is left for the user, and Next stays disabled.
        var stems = NewChartStems.Assign([], ["mix.mp3", "other.mp3"]);

        Assert.AreSequenceEqual(["song", null], stems);
        var problem = NewChartStems.Problems(stems).Single();
        Assert.AreEqual(NewChartStemProblemKind.Unassigned, problem.Kind);
        Assert.AreSequenceEqual([1], problem.Indexes);
    }

    [TestMethod]
    public void AFileNamedSongKeepsTheSongFromAnUnnamedOne()
    {
        var stems = NewChartStems.Assign([], ["mix.mp3", "Song.ogg"]);

        Assert.AreSequenceEqual([null, "song"], stems);
    }

    [TestMethod]
    public void FilesAddedLaterRespectTheStemsAlreadyTaken()
    {
        Assert.AreSequenceEqual([null], NewChartStems.Assign(["song"], ["backing.wav"]));
        Assert.AreSequenceEqual(["song"], NewChartStems.Assign(["guitar", null], ["backing.wav"]));
    }

    [TestMethod]
    public void ANamedStemAlreadyTakenIsStillGivenSoTheClashShows()
    {
        var stems = NewChartStems.Assign(["guitar"], ["guitar.mp3"]);
        Assert.AreSequenceEqual(["guitar"], stems);

        var problem = NewChartStems.Problems(["guitar", "drums", "Guitar"]).Single();
        Assert.AreEqual(NewChartStemProblemKind.Shared, problem.Kind);
        Assert.AreSequenceEqual([0, 2], problem.Indexes);
        Assert.AreEqual("guitar", problem.Stem, ignoreCase: true);
    }

    [TestMethod]
    public void UnassignedFilesAreReportedBeforeClashes()
    {
        var problems = NewChartStems.Problems(["drums", null, "drums"]);

        Assert.HasCount(2, problems);
        Assert.AreEqual(NewChartStemProblemKind.Unassigned, problems[0].Kind);
        Assert.AreEqual(NewChartStemProblemKind.Shared, problems[1].Kind);
    }
}
