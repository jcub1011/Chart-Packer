using ChartPacker.Audio.Playback;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Sng;
using static ChartPacker.Core.Tests.Audio.TestAudio;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class SongAudioTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-songaudio-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void SngTracksAreUnmaskedAndShareOneHandle()
    {
        byte[] guitar = Wav(100, 8000, 1, (n, _) => n / 100f);
        byte[] song = Wav(200, 8000, 2, (_, c) => c == 0 ? 0.25f : -0.25f);
        var builder = new SngBuilder().AddMetadata("name", "Song");
        builder.AddFile("guitar.wav", Masked(builder, guitar)).AddFile("notes.chart", [1, 2, 3]).AddFile("song.wav", Masked(builder, song));
        var entry = WriteSng(builder);

        using (var audio = SongAudio.Open(entry, [Track("guitar.wav"), Track("song.wav")]))
        {
            Assert.IsTrue(audio.Tracks.All(t => t.Error is null));
            Assert.AreEqual(2, audio.Sources.Count);

            float[] guitarSamples = ReadAll(audio.Tracks[0].Source!);
            Assert.AreEqual(100, guitarSamples.Length);
            Assert.AreEqual(0.5f, guitarSamples[50], 1e-4f);
            Assert.AreEqual(-0.25f, ReadAll(audio.Tracks[1].Source!)[1], 1e-4f);
        }

        // Every handle is closed, so the .sng can be replaced, as saving does.
        File.Delete(entry.FullPath);
    }

    [TestMethod]
    public void FolderAndStagedTracksAreReadFromTheirFiles()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "guitar.wav"), Wav(100, 8000, 1, (_, _) => 0.5f));
        string staged = Path.Combine(_root.FullName, "My Bass Take.wav");
        File.WriteAllBytes(staged, Wav(50, 8000, 1, (_, _) => -0.5f));
        var entry = new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song");

        using (var audio = SongAudio.Open(entry, [Track("guitar.wav"), new SongTrack("bass.wav", "bass", staged)]))
        {
            Assert.AreEqual(0.5f, ReadAll(audio.Tracks[0].Source!)[0], 1e-4f);
            Assert.AreEqual(-0.5f, ReadAll(audio.Tracks[1].Source!)[0], 1e-4f);
        }

        File.Delete(Path.Combine(folder, "guitar.wav"));
        File.Delete(staged);
    }

    [TestMethod]
    public void ATrackThatCantBeOpenedIsReportedAndTheOthersStillOpen()
    {
        var builder = new SngBuilder();
        builder.AddFile("guitar.wav", Masked(builder, Wav(100, 8000, 1, (_, _) => 0.1f))).AddFile("bass.ogg", Masked(builder, [1, 2, 3, 4]));
        var entry = WriteSng(builder);

        using var audio = SongAudio.Open(entry, [Track("bass.ogg"), Track("guitar.wav"), Track("drums.wav")]);

        Assert.IsNotNull(audio.Tracks[0].Error);
        Assert.IsNull(audio.Tracks[0].Source);
        Assert.IsNull(audio.Tracks[1].Error);
        Assert.IsNotNull(audio.Tracks[2].Error);
        Assert.AreEqual(1, audio.Sources.Count);
    }

    [TestMethod]
    public void AStagedTrackForAnSngDoesntNeedTheSngOpen()
    {
        string staged = Path.Combine(_root.FullName, "keys.wav");
        File.WriteAllBytes(staged, Wav(10, 8000, 1, (_, _) => 0.1f));
        var entry = new SongEntry(SongEntryKind.SngFile, Path.Combine(_root.FullName, "Missing.sng"), "Missing.sng", "Missing");

        using var audio = SongAudio.Open(entry, [new SongTrack("keys.wav", "keys", staged)]);

        Assert.IsNull(audio.Tracks[0].Error);
    }

    [TestMethod]
    public void AnotherSourceOfATrackReadsTheSameAudioOnAnotherThread()
    {
        byte[] song = Wav(20000, 8000, 2, (n, c) => (c == 0 ? 1 : -1) * (n % 100) / 200f);
        var builder = new SngBuilder();
        builder.AddFile("song.wav", Masked(builder, song));
        var entry = WriteSng(builder);

        using (var audio = SongAudio.Open(entry, [Track("song.wav")]))
        {
            float[] expected = ReadAll(audio.Tracks[0].Source!);

            // Two more sources over the same handle, read at once.
            var reads = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                using var source = audio.OpenSource(audio.Tracks[0].Track);
                return ReadAll(source);
            })).ToArray();
            Task.WaitAll(reads);

            foreach (var read in reads)
                CollectionAssert.AreEqual(expected, read.Result);
        }

        File.Delete(entry.FullPath);
    }

    [TestMethod]
    public void AnotherSourceOfATrackThatIsntInTheSngCantBeOpened()
    {
        var builder = new SngBuilder();
        builder.AddFile("guitar.wav", Masked(builder, Wav(100, 8000, 1, (_, _) => 0.1f)));
        var entry = WriteSng(builder);

        using var audio = SongAudio.Open(entry, [Track("guitar.wav"), Track("drums.wav")]);

        audio.OpenSource(audio.Tracks[0].Track).Dispose();
        Assert.ThrowsExactly<FileNotFoundException>(() => audio.OpenSource(audio.Tracks[1].Track));
    }

    [TestMethod]
    public void AMissingSngFailsToOpen()
    {
        var entry = new SongEntry(SongEntryKind.SngFile, Path.Combine(_root.FullName, "Missing.sng"), "Missing.sng", "Missing");

        Assert.ThrowsExactly<FileNotFoundException>(() => SongAudio.Open(entry, [Track("guitar.wav")]));
    }

    private static SongTrack Track(string name) => new(name, AudioStems.StemOf(name)!, null);

    private static byte[] Masked(SngBuilder builder, byte[] data)
    {
        byte[] copy = [.. data];
        SngMask.Apply(copy, builder.Mask);
        return copy;
    }

    private SongEntry WriteSng(SngBuilder builder)
    {
        string path = Path.Combine(_root.FullName, "Song.sng");
        File.WriteAllBytes(path, builder.Build());
        return new SongEntry(SongEntryKind.SngFile, path, "Song.sng", "Song");
    }
}
