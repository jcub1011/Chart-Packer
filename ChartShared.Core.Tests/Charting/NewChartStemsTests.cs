using ChartShared.Core.Charting;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Charting;

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

    [TestMethod]
    public void ANewChartPlaysEachFileAsTheStemItsNameGives()
    {
        // AC C1.4: Guitar.OGG, Drums.ogg and mix.mp3 are guitar, drums and song, stored in lowercase.
        var audio = NewChartStems.TracksOf([@"C:\a\mix.mp3", @"C:\a\Guitar.OGG", @"C:\a\Drums.ogg"]);

        Assert.AreSequenceEqual(
            [
                new SongTrack("song.mp3", "song", @"C:\a\mix.mp3"),
                new SongTrack("guitar.ogg", "guitar", @"C:\a\Guitar.OGG"),
                new SongTrack("drums.ogg", "drums", @"C:\a\Drums.ogg"),
            ],
            audio.Tracks.ToList());
        Assert.IsEmpty(audio.NotAudio);
        Assert.IsEmpty(audio.NoStem);
    }

    [TestMethod]
    public void FilesWithNoStemLeftOrNotAudioAreLeftOut()
    {
        // AC C1.4: of mix.mp3 and other.mp3, one is song and the other has none.
        var audio = NewChartStems.TracksOf([@"C:\a\mix.mp3", @"C:\a\other.mp3", @"C:\a\notes.txt", @"C:\b\Guitar.ogg", @"C:\c\guitar.wav", @"C:\a\mix.mp3"]);

        Assert.AreSequenceEqual(["song", "guitar"], audio.Tracks.Select(t => t.Stem).ToList());
        Assert.AreSequenceEqual([@"C:\a\notes.txt"], audio.NotAudio.ToList());
        Assert.AreSequenceEqual([@"C:\a\other.mp3", @"C:\c\guitar.wav"], audio.NoStem.ToList());
    }
}
