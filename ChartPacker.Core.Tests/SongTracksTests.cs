using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;

namespace ChartPacker.Core.Tests;

[TestClass]
public sealed class SongTracksTests
{
    [TestMethod]
    [DataRow("guitar.ogg", "guitar")]
    [DataRow("Guitar.OGG", "guitar")]
    [DataRow("drums_4.opus", "drums_4")]
    [DataRow("preview.mp3", "preview")]
    [DataRow("song.wav", "song")]
    [DataRow("guitar_2.ogg", null)]
    [DataRow("stems/guitar.ogg", null)]
    [DataRow("guitar.flac", null)]
    [DataRow("notes.mid", null)]
    public void StemOfRecognisesRegisteredAudio(string name, string? stem) =>
        Assert.AreEqual(stem, AudioStems.StemOf(name));

    [TestMethod]
    public void EveryStemInEveryFormatIsInTheFilenameRegistry()
    {
        foreach (string stem in AudioStems.Stems)
        {
            foreach (string extension in AudioStems.Extensions)
                Assert.IsTrue(FilenameRules.IsRegistered(stem + extension), stem + extension);
        }

        Assert.HasCount(15, AudioStems.Stems);
    }

    [TestMethod]
    public void TracksAreTheRegisteredStemsInStemOrder()
    {
        var tracks = SongTracks.Resolve(
            ["notes.chart", "vocals.ogg", "song.ogg", "guitar.opus", "album.png", "guitar_old.ogg", "stems/bass.ogg", "Drums_1.ogg"],
            draft: null);

        Assert.AreSequenceEqual(["song.ogg", "guitar.opus", "Drums_1.ogg", "vocals.ogg"], [.. tracks.Select(t => t.StoredName)]);
        Assert.AreSequenceEqual(["song", "guitar", "drums_1", "vocals"], [.. tracks.Select(t => t.Stem)]);
        Assert.IsTrue(tracks.All(t => !t.IsStaged));
    }

    [TestMethod]
    public void TheDraftsRemovalsAndAddsAreApplied()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("bass.ogg");
        draft.AddFile(new AddedFile("keys.opus", @"C:\takes\keys.opus", 10));
        draft.AddFile(new AddedFile("Guitar.ogg", @"C:\takes\guitar.ogg", 10));
        draft.AddFile(new AddedFile("readme.txt", @"C:\takes\readme.txt", 10));

        var tracks = SongTracks.Resolve(["guitar.ogg", "bass.ogg", "song.ogg"], draft);

        Assert.AreSequenceEqual(["song.ogg", "Guitar.ogg", "keys.opus"], [.. tracks.Select(t => t.StoredName)]);
        Assert.IsFalse(tracks[0].IsStaged);
        Assert.AreEqual(@"C:\takes\guitar.ogg", tracks[1].StagedSourcePath);
        Assert.AreEqual(@"C:\takes\keys.opus", tracks[2].StagedSourcePath);
    }

    [TestMethod]
    public void FilesOfStemFindsItInEveryFormat()
    {
        var files = SongTracks.FilesOfStem(["guitar.ogg", "Guitar.MP3", "guitar_old.ogg", "bass.ogg", "album.png"], "guitar");

        Assert.AreSequenceEqual(["guitar.ogg", "Guitar.MP3"], [.. files]);
    }
}
