using System.Text;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Library;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Audio;
using ChartPacker.Core.Tests.Charts;

namespace ChartPacker.Core.Tests.Merging;

[TestClass]
public sealed class SongMergerTests
{
    private const string Ogg = "chirp-stereo-44100.ogg";

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-merger-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task AnalysisFindsThePartsAndTheMetadataThatDiffers()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);

        CollectionAssert.AreEqual(new[] { "guitar", "bass", "drums" }, analysis.Parts.Select(p => p.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1 }, analysis.SourcesWith("drums").ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, analysis.SourcesWith("guitar").ToArray());
        Assert.AreEqual(MergeChartFormat.Midi, analysis.Sources[0].Format);
        Assert.AreEqual(MergeChartFormat.Chart, analysis.Sources[1].Format);

        // The charters, difficulties, pro_drums and preview are worked out, so only the genre is a choice.
        Assert.AreEqual("genre", analysis.Conflicts.Single().Key);
        CollectionAssert.AreEqual(new[] { "Rock", "Metal" }, analysis.Conflicts.Single().Values.ToArray());

        var drums = analysis.Sources[0].Parts.Single(p => p.Part.Id == "drums");
        Assert.AreEqual("3", drums.Difficulty);
        CollectionAssert.AreEqual(new[] { ChartDifficulty.Expert }, drums.Difficulties.ToArray());
        CollectionAssert.AreEqual(new[] { new NoteCount(ChartDifficulty.Expert, 0, 2) }, drums.NoteCounts.ToArray());
    }

    [TestMethod]
    public async Task AnalysisReadsTheDifficultiesAndNotesOfPartsTheNoteReaderSkips()
    {
        var folder = _root.CreateSubdirectory("pro keys");
        File.WriteAllText(Path.Combine(folder.FullName, "song.ini"), "[song]\nname = Song\n");
        File.WriteAllBytes(Path.Combine(folder.FullName, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART REAL_KEYS_X").Note(0, 60).Note(480, 62)
            .Track("PART REAL_KEYS_E").Note(0, 60)
            .Track("PART VOCALS").Note(0, 60)
            .Build());

        var analysis = await SongMergeAnalysis.LoadAsync([new SongEntry(SongEntryKind.SongFolder, folder.FullName, folder.Name, folder.Name)]);

        var proKeys = analysis.Sources[0].Parts.Single(p => p.Part.Id == "pro_keys");
        CollectionAssert.AreEqual(new[] { ChartDifficulty.Easy, ChartDifficulty.Expert }, proKeys.Difficulties.ToArray());
        CollectionAssert.AreEqual(
            new[] { new NoteCount(ChartDifficulty.Easy, 0, 1), new NoteCount(ChartDifficulty.Expert, 0, 2) },
            proKeys.NoteCounts.ToArray());
        var vocals = analysis.Sources[0].Parts.Single(p => p.Part.Id == "vocals");
        Assert.IsEmpty(vocals.Difficulties);
        CollectionAssert.AreEqual(new[] { new NoteCount(null, 0, 1) }, vocals.NoteCounts.ToArray());
    }

    [TestMethod]
    public void LegacyKeysAreReadUnderTheirReplacements()
    {
        var pairs = SongMergeAnalysis.Normalize([P("frets", "Old"), P("Charter", "New"), P("Name", "Song"), P("genre", " ")]);

        CollectionAssert.AreEqual(new[] { P("charter", "New"), P("name", "Song") }, pairs);
    }

    [TestMethod]
    public void KeysAreWorkedOutFromWhereTheyBelong()
    {
        Assert.AreEqual(MergeKeyRule.Part, SongMergeAnalysis.RuleOf("diff_drums"));
        Assert.AreEqual(MergeKeyRule.Part, SongMergeAnalysis.RuleOf("pro_drums"));
        Assert.AreEqual(MergeKeyRule.Charter, SongMergeAnalysis.RuleOf("charter"));
        Assert.AreEqual(MergeKeyRule.Audio, SongMergeAnalysis.RuleOf("preview_start_time"));
        Assert.AreEqual(MergeKeyRule.Video, SongMergeAnalysis.RuleOf("video_start_time"));
        Assert.AreEqual(MergeKeyRule.Base, SongMergeAnalysis.RuleOf("hopo_frequency"));
        Assert.AreEqual(MergeKeyRule.Chosen, SongMergeAnalysis.RuleOf("genre"));
    }

    [TestMethod]
    public async Task MergedSongTakesEachPartWithItsDifficultyFromItsSource()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = new SongMergePlan(
            analysis,
            BaseSource: 0,
            Parts: new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 },
            Metadata: new Dictionary<string, int?> { ["genre"] = 1 },
            AudioSource: 1,
            AlbumArtSource: 0,
            BackgroundSource: null,
            VideoSource: null);
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo());

        Assert.AreEqual(Path.Combine(output, "Band - Song.sng"), result.OutputPath);
        var read = SngReader.Read(result.OutputPath);
        Assert.IsFalse(read.HasErrors);
        var metadata = read.Metadata.ToDictionary(p => p.Key, p => p.Value);
        Assert.AreEqual("Song", metadata["name"]);
        Assert.AreEqual("Alice, <color=red>Bob</color>", metadata["charter"]);
        Assert.AreEqual("2", metadata["diff_bass"]);
        Assert.AreEqual("5", metadata["diff_drums"]);
        Assert.AreEqual("4", metadata["diff_guitar"]);
        Assert.AreEqual("Metal", metadata["genre"]);
        Assert.AreEqual("True", metadata["pro_drums"]);
        Assert.IsFalse(metadata.ContainsKey("preview_start_time"));
        Assert.IsTrue(long.Parse(metadata["song_length"]) > SongLengthEstimate.BufferMs);

        CollectionAssert.AreEquivalent(
            new[] { "notes.mid", "guitar.ogg", "drums.ogg", "album.png" },
            read.Files.Select(f => f.Name).ToArray());

        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "Band - Song.sng", "Band - Song");
        var chart = ChartReader.Read((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes))!, "notes.mid", new ChartReadOptions(ProDrums: true));
        CollectionAssert.AreEqual(new[] { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums }, chart.Instruments.ToArray());
        // The .chart's drums were at 192 ticks a beat; the base's at 480, both at 120 BPM.
        CollectionAssert.AreEqual(
            new[] { new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None), new ChartNote(240, 0, 1, NoteFlags.Cymbal) },
            ChartReaderTests.Notes(chart, ChartInstrument.Drums));
        StringAssert.Contains(Encoding.Latin1.GetString((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes))!), "[section Intro]");
    }

    [TestMethod]
    public async Task PartsLeftOutTakeTheirDifficultiesWithThem()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });

        var metadata = SongMerger.BuildMetadata(plan).ToDictionary(p => p.Key, p => p.Value);

        Assert.AreEqual("3", metadata["diff_drums"]);
        Assert.IsFalse(metadata.ContainsKey("diff_bass"));
        Assert.IsFalse(metadata.ContainsKey("diff_guitar"));
        Assert.IsFalse(metadata.ContainsKey("pro_drums"));
        Assert.AreEqual("Rock", metadata["genre"]);
        Assert.AreEqual("1000", metadata["preview_start_time"]);
    }

    [TestMethod]
    public async Task ExistingOutputIsSkippedOrRenamed()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        string output = _root.CreateSubdirectory("out").FullName;
        File.WriteAllText(Path.Combine(output, "Band - Song.sng"), "taken");

        Assert.IsNull(SongMerger.TargetFor(plan, output, OverwritePolicy.Skip));
        await Assert.ThrowsExactlyAsync<IOException>(() => SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo()));

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Rename, new NoVideo());
        Assert.AreEqual(Path.Combine(output, "Band - Song (2).sng"), result.OutputPath);
        Assert.AreEqual("taken", File.ReadAllText(Path.Combine(output, "Band - Song.sng")));
    }

    [TestMethod]
    public async Task ASourceIsNeverWrittenOver()
    {
        var sng = await DrumsAndGuitarSngAsync("Band - Song");
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), sng]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        byte[] before = File.ReadAllBytes(sng.FullPath);

        await Assert.ThrowsExactlyAsync<IOException>(() => SongMerger.MergeAsync(plan, Path.GetDirectoryName(sng.FullPath)!, OverwritePolicy.Overwrite, new NoVideo()));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(sng.FullPath));
    }

    [TestMethod]
    public async Task AFailedMergeLeavesNothingBehind()
    {
        // A ';' can't be packed into a value.
        var a = DrumsAndBass(genre: "Rock;Pop");
        var analysis = await SongMergeAnalysis.LoadAsync([a, await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        string output = _root.CreateSubdirectory("out").FullName;
        var stagingBefore = StagingFolders();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo()));

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(output));
        CollectionAssert.IsSubsetOf(StagingFolders(), stagingBefore);
    }

    [TestMethod]
    public async Task OffsetsAreAppliedToTheMergedChartButNotToAPreview()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var drums = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["bass"] = 0 }) with
        {
            ChartOffsets = new Dictionary<ChartTrackKey, int> { [drums] = 500 },
        };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo());
        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "merged.sng", "merged");
        var chart = ChartReader.Read((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes))!, "notes.mid");

        // Half a second at 120 BPM is a beat, 480 ticks; the bass didn't move.
        CollectionAssert.AreEqual(new long[] { 480, 960 }, ChartReaderTests.Notes(chart, ChartInstrument.Drums).Select(n => n.Tick).ToArray());
        CollectionAssert.AreEqual(new long[] { 0, 480 }, ChartReaderTests.Notes(chart, ChartInstrument.Bass).Select(n => n.Tick).ToArray());

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>());
        var preview = ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid");
        CollectionAssert.AreEqual(new long[] { 0, 480 }, ChartReaderTests.Notes(preview, ChartInstrument.Drums).Select(n => n.Tick).ToArray());
    }

    [TestMethod]
    public async Task VocalsOffsetIsAppliedToTheMergedChartButNotToAPreview()
    {
        var withVocals = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(withVocals.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 96).Note(480, 97)
            .Track("PART VOCALS").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build());
        var analysis = await SongMergeAnalysis.LoadAsync([withVocals, await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["vocals"] = 0 }) with { VocalsOffset = 500 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo());
        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "merged.sng", "merged");
        byte[] merged = (await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes))!;

        // Half a second at 120 BPM is a beat, 480 ticks; the drums didn't move.
        Assert.AreEqual(480L, VocalReader.Read(merged, "notes.mid").FirstTick);
        CollectionAssert.AreEqual(new long[] { 0, 480 }, ChartReaderTests.Notes(ChartReader.Read(merged, "notes.mid"), ChartInstrument.Drums).Select(n => n.Tick).ToArray());

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>());
        Assert.AreEqual(0L, VocalReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid").FirstTick);
    }

    [TestMethod]
    public async Task AudioAndVideoOffsetsGoIntoTheMergedSongIniButNotIntoAPreview()
    {
        var withVideo = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(withVideo.FullPath, "video.mp4"), [0, 0, 0, 0]);
        var analysis = await SongMergeAnalysis.LoadAsync([withVideo, await DrumsAndGuitarSngAsync()]);

        // Audio later is a smaller delay, a video later a smaller start; neither song sets either.
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { VideoSource = 0, AudioOffset = 120, VideoOffset = -250 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo());
        var metadata = SngReader.Read(result.OutputPath).Metadata.ToDictionary(p => p.Key, p => p.Value);
        Assert.AreEqual("-120", metadata["delay"]);
        Assert.AreEqual("250", metadata["video_start_time"]);

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>());
        string ini = File.ReadAllText(Path.Combine(folder, "song.ini"));
        Assert.IsFalse(ini.Contains("delay", StringComparison.Ordinal));
        Assert.IsFalse(ini.Contains("video_start_time", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AVideoOffsetWithoutAVideoIsLeftOut()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { VideoOffset = 300 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo());

        Assert.IsFalse(SngReader.Read(result.OutputPath).Metadata.Any(p => p.Key == "video_start_time"));
    }

    [TestMethod]
    public async Task StagingAgainReplacesOnlyWhatChanged()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { AlbumArtSource = 0 };
        string folder = _root.CreateSubdirectory("preview").FullName;
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await SongMerger.StageAsync(plan, folder, new NoVideo(), staged);
        CollectionAssert.AreEquivalent(new[] { "notes.mid", "song.ini", "song.ogg", "album.png" }, Names(folder));

        // A file that would come from the same place isn't copied again.
        File.WriteAllBytes(Path.Combine(folder, "album.png"), [1, 2, 3]);
        await SongMerger.StageAsync(plan with { AudioSource = 1 }, folder, new NoVideo(), staged);

        CollectionAssert.AreEquivalent(new[] { "notes.mid", "song.ini", "guitar.ogg", "drums.ogg", "album.png" }, Names(folder));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(folder, "album.png")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(folder, "song.ini")), "song_length = ");

        await SongMerger.StageAsync(plan with { AlbumArtSource = null }, folder, new NoVideo(), staged);
        Assert.IsFalse(File.Exists(Path.Combine(folder, "album.png")));
    }

    [TestMethod]
    public async Task PlanThatNamesAPartTheSourceLacksIsRejected()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()]);
        var plan = Plan(analysis, new Dictionary<string, int> { ["guitar"] = 0 });

        Assert.ThrowsExactly<ArgumentException>(() => SongMerger.BuildMetadata(plan));
    }

    [TestMethod]
    public void FileNameHasNoTagsOrCharactersFilesCantHave()
    {
        Assert.AreEqual("AC_DC - Back _In_ Black", SongMerger.FileNameFor([P("artist", "AC/DC"), P("name", "<color=red>Back \"In\" Black</color>")], "fallback"));
        Assert.AreEqual("fallback", SongMerger.FileNameFor([], "fallback"));
    }

    private static SongMergePlan Plan(SongMergeAnalysis analysis, Dictionary<string, int> parts) =>
        new(analysis, 0, parts, new Dictionary<string, int?>(), 0, null, null, null);

    private static string[] Names(string folder) => [.. Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>()];

    private static string[] StagingFolders() =>
        [.. Directory.EnumerateDirectories(Path.GetTempPath(), "chartpacker-merge-*")];

    /// <summary>A folder song: pro-less drums and bass in MIDI at 480 ticks a beat, the full mix, and album art.</summary>
    private SongEntry DrumsAndBass(string genre = "Rock")
    {
        var folder = _root.CreateSubdirectory("A " + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(folder.FullName, "song.ini"), $"""
            [song]
            name = Song
            artist = Band
            charter = Alice
            genre = {genre}
            year = 2001
            diff_drums = 3
            diff_bass = 2
            preview_start_time = 1000
            """);
        File.WriteAllBytes(Path.Combine(folder.FullName, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("EVENTS").Text(0, "[section Intro]")
            .Track("PART DRUMS").Note(0, 96).Note(480, 97)
            .Track("PART BASS").Note(0, 96).Note(480, 97)
            .Build());
        File.Copy(TestAudio.Fixture(Ogg), Path.Combine(folder.FullName, "song.ogg"));
        File.WriteAllBytes(Path.Combine(folder.FullName, "album.png"), [0x89, 0x50, 0x4E, 0x47]);
        return new SongEntry(SongEntryKind.SongFolder, folder.FullName, folder.Name, folder.Name);
    }

    /// <summary>A <c>.sng</c>: pro drums and guitar in a <c>.chart</c> at 192 ticks a beat, and guitar and drum stems.</summary>
    private async Task<SongEntry> DrumsAndGuitarSngAsync(string name = "B")
    {
        var folder = _root.CreateSubdirectory("B-source " + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(folder.FullName, "song.ini"), """
            [song]
            name = Song
            artist = Band
            charter = <color=red>Bob</color>
            genre = Metal
            year = 2001
            diff_drums = 5
            diff_guitar = 4
            """);
        File.WriteAllText(Path.Combine(folder.FullName, "notes.chart"), """
            [Song]
            {
              Resolution = 192
            }
            [SyncTrack]
            {
              0 = B 120000
            }
            [ExpertSingle]
            {
              0 = N 0 0
              192 = N 1 0
            }
            [ExpertDrums]
            {
              0 = N 0 0
              96 = N 2 0
              96 = N 66 0
            }
            """);
        File.Copy(TestAudio.Fixture(Ogg), Path.Combine(folder.FullName, "guitar.ogg"));
        File.Copy(TestAudio.Fixture(Ogg), Path.Combine(folder.FullName, "drums.ogg"));

        var packs = _root.CreateSubdirectory("packs " + Guid.NewGuid().ToString("N")[..6]);
        string path = Path.Combine(packs.FullName, name + ".sng");
        await Packer.PackAsync(await PackPlanner.PlanAsync(folder.FullName), path, overwrite: false);
        return new SongEntry(SongEntryKind.SngFile, path, name + ".sng", name);
    }

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);

    private sealed class NoVideo : IVideoDurationProbe
    {
        public TimeSpan? Duration(Stream stream, string name) => null;
    }
}
