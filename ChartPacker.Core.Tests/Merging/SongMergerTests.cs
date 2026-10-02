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
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);

        Assert.AreSequenceEqual(["guitar", "bass", "drums"], [.. analysis.Parts.Select(p => p.Id)]);
        Assert.AreSequenceEqual([0, 1], [.. analysis.SourcesWith("drums")]);
        Assert.AreSequenceEqual([1], [.. analysis.SourcesWith("guitar")]);
        Assert.AreEqual(MergeChartFormat.Midi, analysis.Sources[0].Format);
        Assert.AreEqual(MergeChartFormat.Chart, analysis.Sources[1].Format);

        // The charters, difficulties, pro_drums and preview are worked out, so only the genre is a choice.
        Assert.AreEqual("genre", analysis.Conflicts.Single().Key);
        Assert.AreSequenceEqual(["Rock", "Metal"], analysis.Conflicts.Single().Values.ToArray());

        var drums = analysis.Sources[0].Parts.Single(p => p.Part.Id == "drums");
        Assert.AreEqual("3", drums.Difficulty);
        Assert.AreSequenceEqual([ChartDifficulty.Expert], [.. drums.Difficulties]);
        Assert.AreSequenceEqual([new NoteCount(ChartDifficulty.Expert, 0, 2)], [.. drums.NoteCounts]);
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

        var analysis = await SongMergeAnalysis.LoadAsync([new SongEntry(SongEntryKind.SongFolder, folder.FullName, folder.Name, folder.Name)], TestContext.CancellationToken);

        var proKeys = analysis.Sources[0].Parts.Single(p => p.Part.Id == "pro_keys");
        Assert.AreSequenceEqual([ChartDifficulty.Easy, ChartDifficulty.Expert], [.. proKeys.Difficulties]);
        Assert.AreSequenceEqual(
            [new NoteCount(ChartDifficulty.Easy, 0, 1), new NoteCount(ChartDifficulty.Expert, 0, 2)], [.. proKeys.NoteCounts]);
        var vocals = analysis.Sources[0].Parts.Single(p => p.Part.Id == "vocals");
        Assert.IsEmpty(vocals.Difficulties);
        Assert.AreSequenceEqual([new NoteCount(null, 0, 1)], [.. vocals.NoteCounts]);
    }

    [TestMethod]
    public void LegacyKeysAreReadUnderTheirReplacements()
    {
        var pairs = SongMergeAnalysis.Normalize([P("frets", "Old"), P("Charter", "New"), P("Name", "Song"), P("genre", " ")]);

        Assert.AreSequenceEqual([P("charter", "New"), P("name", "Song")], pairs);
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
        Assert.AreEqual(MergeKeyRule.FromChart, SongMergeAnalysis.RuleOf("Sustain_Cutoff_Threshold"));
        Assert.AreEqual(MergeKeyRule.FromChart, SongMergeAnalysis.RuleOf("multiplier_note"));
        Assert.AreEqual(MergeKeyRule.Delay, SongMergeAnalysis.RuleOf("delay_seconds"));
        Assert.AreEqual(MergeKeyRule.Chosen, SongMergeAnalysis.RuleOf("genre"));
    }

    [TestMethod]
    public async Task MergedSongTakesEachPartWithItsDifficultyFromItsSource()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
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

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(output, "Band - Song.sng"), result.OutputPath);
        var read = SngReader.Read(result.OutputPath, TestContext.CancellationToken);
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
        Assert.IsGreaterThan(SongLengthEstimate.BufferMs, long.Parse(metadata["song_length"]));

        Assert.AreSequenceEqual(
            ["notes.mid", "guitar.ogg", "drums.ogg", "album.png"], [.. read.Files.Select(f => f.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);

        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "Band - Song.sng", "Band - Song");
        var chart = ChartReader.Read((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes, TestContext.CancellationToken))!, "notes.mid", new ChartReadOptions(ProDrums: true));
        Assert.AreSequenceEqual([ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums], [.. chart.Instruments]);
        // The .chart's drums were at 192 ticks a beat; the base's at 480, both at 120 BPM.
        Assert.AreSequenceEqual(
            [new ChartNote(0, 0, ChartNote.OpenLane, NoteFlags.None), new ChartNote(240, 0, 1, NoteFlags.Cymbal)], ChartReaderTests.Notes(chart, ChartInstrument.Drums));
        Assert.Contains("[section Intro]", Encoding.Latin1.GetString((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes, TestContext.CancellationToken))!));
    }

    [TestMethod]
    public async Task PartsLeftOutTakeTheirDifficultiesWithThem()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
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
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        string output = _root.CreateSubdirectory("out").FullName;
        File.WriteAllText(Path.Combine(output, "Band - Song.sng"), "taken");

        Assert.IsNull(SongMerger.TargetFor(plan, output, OverwritePolicy.Skip));
        await Assert.ThrowsExactlyAsync<IOException>(() => SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken));

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Rename, new NoVideo(), TestContext.CancellationToken);
        Assert.AreEqual(Path.Combine(output, "Band - Song (2).sng"), result.OutputPath);
        Assert.AreEqual("taken", File.ReadAllText(Path.Combine(output, "Band - Song.sng")));
    }

    [TestMethod]
    public async Task ASourceIsNeverWrittenOver()
    {
        var sng = await DrumsAndGuitarSngAsync("Band - Song");
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), sng], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        byte[] before = File.ReadAllBytes(sng.FullPath);

        await Assert.ThrowsExactlyAsync<IOException>(() => SongMerger.MergeAsync(plan, Path.GetDirectoryName(sng.FullPath)!, OverwritePolicy.Overwrite, new NoVideo(), TestContext.CancellationToken));

        Assert.AreSequenceEqual(before, File.ReadAllBytes(sng.FullPath));
    }

    [TestMethod]
    public async Task AFailedMergeLeavesNothingBehind()
    {
        // A ';' can't be packed into a value.
        var a = DrumsAndBass(genre: "Rock;Pop");
        var analysis = await SongMergeAnalysis.LoadAsync([a, await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 });
        string output = _root.CreateSubdirectory("out").FullName;
        var stagingBefore = StagingFolders();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken));

        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(output));
        CollectionAssert.IsSubsetOf(StagingFolders(), stagingBefore);
    }

    [TestMethod]
    public async Task OffsetsAreAppliedToTheMergedChartButNotToAPreview()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var drums = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["bass"] = 0 }) with
        {
            ChartOffsets = new Dictionary<ChartTrackKey, int> { [drums] = 500 },
        };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);
        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "merged.sng", "merged");
        var chart = ChartReader.Read((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes, TestContext.CancellationToken))!, "notes.mid");

        // Half a second at 120 BPM is a beat, 480 ticks; the bass didn't move.
        Assert.AreSequenceEqual([480, 960], [.. ChartReaderTests.Notes(chart, ChartInstrument.Drums).Select(n => n.Tick)]);
        Assert.AreSequenceEqual([0, 480], [.. ChartReaderTests.Notes(chart, ChartInstrument.Bass).Select(n => n.Tick)]);

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        var preview = ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid");
        Assert.AreSequenceEqual([0, 480], [.. ChartReaderTests.Notes(preview, ChartInstrument.Drums).Select(n => n.Tick)]);
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
        var analysis = await SongMergeAnalysis.LoadAsync([withVocals, await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["vocals"] = 0 }) with { VocalsOffset = 500 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);
        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "merged.sng", "merged");
        byte[] merged = (await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes, TestContext.CancellationToken))!;

        // Half a second at 120 BPM is a beat, 480 ticks; the drums didn't move.
        Assert.AreEqual(480L, VocalReader.Read(merged, "notes.mid").FirstTick);
        Assert.AreSequenceEqual([0, 480], [.. ChartReaderTests.Notes(ChartReader.Read(merged, "notes.mid"), ChartInstrument.Drums).Select(n => n.Tick)]);

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        Assert.AreEqual(0L, VocalReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid").FirstTick);
    }

    [TestMethod]
    public async Task AudioAndVideoOffsetsGoIntoTheMergedSongIniButNotIntoAPreview()
    {
        var withVideo = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(withVideo.FullPath, "video.mp4"), [0, 0, 0, 0]);
        var analysis = await SongMergeAnalysis.LoadAsync([withVideo, await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);

        // Audio later is a smaller delay, a video later a smaller start; neither song sets either.
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { VideoSource = 0, AudioOffset = 120, VideoOffset = -250 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);
        var metadata = SngReader.Read(result.OutputPath, TestContext.CancellationToken).Metadata.ToDictionary(p => p.Key, p => p.Value);
        Assert.AreEqual("-120", metadata["delay"]);
        Assert.AreEqual("250", metadata["video_start_time"]);

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        string ini = File.ReadAllText(Path.Combine(folder, "song.ini"));
        Assert.IsFalse(ini.Contains("delay", StringComparison.Ordinal));
        Assert.IsFalse(ini.Contains("video_start_time", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SourceOffsetsMoveThePartsFromTheirSourceInTheMergeAndThePreview()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 }) with
        {
            SourceOffsets = new Dictionary<int, int> { [1] = 500 },
        };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);
        var entry = new SongEntry(SongEntryKind.SngFile, result.OutputPath, "merged.sng", "merged");
        var chart = ChartReader.Read((await SongFile.ReadAsync(entry, "notes.mid", ChartReader.MaxBytes, TestContext.CancellationToken))!, "notes.mid");

        // Half a second at 120 BPM is a beat, 480 ticks; the base's bass and delay didn't move.
        Assert.AreSequenceEqual([480, 720], [.. ChartReaderTests.Notes(chart, ChartInstrument.Drums).Select(n => n.Tick)]);
        Assert.AreSequenceEqual([480, 960], [.. ChartReaderTests.Notes(chart, ChartInstrument.Guitar).Select(n => n.Tick)]);
        Assert.AreSequenceEqual([0, 480], [.. ChartReaderTests.Notes(chart, ChartInstrument.Bass).Select(n => n.Tick)]);
        Assert.DoesNotContain(p => p.Key == "delay", SngReader.Read(result.OutputPath, TestContext.CancellationToken).Metadata);

        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        var preview = ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid");
        Assert.AreSequenceEqual([480, 720], [.. ChartReaderTests.Notes(preview, ChartInstrument.Drums).Select(n => n.Tick)]);
    }

    [TestMethod]
    public async Task TheBaseSourcesOffsetMovesTheDelayAndTheVideosSourcesTheVideoStart()
    {
        var withVideo = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(withVideo.FullPath, "video.mp4"), [0, 0, 0, 0]);
        var analysis = await SongMergeAnalysis.LoadAsync([withVideo, await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);

        // The audio is the second song's; the first's chart and video are 200 ms early against it.
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["guitar"] = 1 }) with
        {
            AudioSource = 1,
            VideoSource = 0,
            SourceOffsets = new Dictionary<int, int> { [0] = 200 },
        };

        var metadata = SongMerger.BuildMetadata(plan).ToDictionary(p => p.Key, p => p.Value);
        Assert.AreEqual("200", metadata["delay"]);
        Assert.AreEqual("-200", metadata["video_start_time"]);

        // The guitar's song isn't moved, so it's placed 200 ms earlier against the base's delay than the drums are.
        string folder = _root.CreateSubdirectory("preview").FullName;
        await SongMerger.StageAsync(plan, folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        var preview = ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid");
        Assert.AreSequenceEqual([0, 480], [.. ChartReaderTests.Notes(preview, ChartInstrument.Drums).Select(n => n.Tick)]);
        Assert.AreSequenceEqual([0, 288], [.. ChartReaderTests.Notes(preview, ChartInstrument.Guitar).Select(n => n.Tick)]);
        Assert.Contains("delay = 200", File.ReadAllText(Path.Combine(folder, "song.ini")));
    }

    [TestMethod]
    public async Task PreviewShiftsShowChoosingAnOffsetLikeStagingIt()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var staged = Plan(analysis, new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 });
        var pending = staged with { SourceOffsets = new Dictionary<int, int> { [1] = 500 } };

        var shifts = SongMerger.PreviewShifts(staged, pending);
        Assert.IsNotNull(shifts);

        // The second song's drums and guitar move half a second later; the base's bass doesn't move at all.
        Assert.AreEqual(500, shifts.Tracks[new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert)]);
        Assert.AreEqual(500, shifts.Tracks[new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Easy)]);
        Assert.IsFalse(shifts.Tracks.Keys.Any(k => k.Instrument == ChartInstrument.Bass));
        Assert.AreEqual(0, shifts.VocalsMs);

        // Moving what's staged by the shifts shows what staging the choice stages, to a few milliseconds a note.
        var options = ChartReadOptions.From(SongMerger.BuildMetadata(pending));
        var live = ChartReader.Read(ChartShifter.Shift(SongMerger.MergedChart(staged).Midi, "notes.mid", shifts.Tracks, options).Bytes, "notes.mid", options);
        var fresh = ChartReader.Read(SongMerger.MergedChart(pending).Midi, "notes.mid", options);
        Assert.AreSequenceEqual(
            [.. fresh.Tracks.Keys.OrderBy(k => k.Instrument).ThenBy(k => k.Difficulty)],
            [.. live.Tracks.Keys.OrderBy(k => k.Instrument).ThenBy(k => k.Difficulty)]);
        foreach (var key in fresh.Tracks.Keys)
        {
            var want = fresh.Tracks[key].Notes;
            var got = live.Tracks[key].Notes;
            Assert.AreEqual(want.Count, got.Count, key.ToString());
            for (int i = 0; i < want.Count; i++)
            {
                double off = Math.Abs(fresh.Tempo.SecondsAt(want[i].Tick) - live.Tempo.SecondsAt(got[i].Tick));
                Assert.IsTrue(off < 0.005, $"{key} note {i} is {off:0.000} s apart");
            }
        }
    }

    [TestMethod]
    public async Task PreviewShiftsShowChoosingTheBaseSourcesOffsetLikeStagingIt()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var staged = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["guitar"] = 1 });
        var pending = staged with { SourceOffsets = new Dictionary<int, int> { [0] = 200 } };

        var shifts = SongMerger.PreviewShifts(staged, pending);
        Assert.IsNotNull(shifts);

        // Moving the base later moves the start with it: the guitar is placed 200 ms earlier against the
        // delay, while the base's own drums stay where they are. (Shifting the staged chart itself that far
        // would move the guitar's first note before the start, which the highway reports instead of showing.)
        Assert.AreEqual(-200, shifts.Tracks[new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert)]);
        Assert.IsFalse(shifts.Tracks.Keys.Any(k => k.Instrument == ChartInstrument.Drums));
        Assert.AreEqual("200", SongMerger.BuildMetadata(pending).First(p => p.Key == "delay").Value);
    }

    [TestMethod]
    public async Task PreviewShiftsAreFromWhereTheStagingAlreadyLinedTheSongsUp()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 });
        var staged = plan with { SourceOffsets = new Dictionary<int, int> { [1] = 300 } };
        var pending = plan with { SourceOffsets = new Dictionary<int, int> { [1] = 500 } };

        var shifts = SongMerger.PreviewShifts(staged, pending);
        Assert.IsNotNull(shifts);

        // Only what's moved since the staging: the 300 ms it already has isn't moved again.
        Assert.AreEqual(200, shifts.Tracks[new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert)]);
        Assert.AreEqual(200, shifts.Tracks[new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert)]);
        Assert.IsEmpty(SongMerger.PreviewShifts(pending, pending)!.Tracks);
    }

    [TestMethod]
    public async Task PreviewShiftsMoveTheVocalsWithTheSongTheyComeFrom()
    {
        var withVocals = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(withVocals.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 96).Note(480, 97)
            .Track("PART VOCALS").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build());
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), withVocals], TestContext.CancellationToken);
        var staged = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["vocals"] = 1 });
        var pending = staged with { SourceOffsets = new Dictionary<int, int> { [1] = 250 } };

        var shifts = SongMerger.PreviewShifts(staged, pending);
        Assert.IsNotNull(shifts);

        // The base's drums stay; the vocals move with the second song.
        Assert.AreEqual(250, shifts.VocalsMs);
        Assert.IsEmpty(shifts.Tracks);
    }

    [TestMethod]
    public async Task PreviewShiftsFallBackWhenVocalsAndHarmoniesFromDifferentSourcesMoveApart()
    {
        // An empty PART VOCALS gives way to a harmony with lyrics in the lyrics lane,
        // so shifting by the vocals' source would place the shown words wrongly.
        var lyricLessVocals = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(lyricLessVocals.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Note(0, 60)
            .Build());
        var harmoniesWithLyrics = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(harmoniesWithLyrics.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("HARM1").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build());
        var analysis = await SongMergeAnalysis.LoadAsync([lyricLessVocals, harmoniesWithLyrics], TestContext.CancellationToken);
        var staged = Plan(analysis, new Dictionary<string, int> { ["vocals"] = 0, ["harmonies"] = 1 });
        var pending = staged with { SourceOffsets = new Dictionary<int, int> { [1] = 250 } };

        Assert.IsNull(SongMerger.PreviewShifts(staged, pending));
    }

    [TestMethod]
    public async Task PreviewShiftsStayLiveWhenVocalsAndHarmoniesMoveTogether()
    {
        var lyricLessVocals = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(lyricLessVocals.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 96)
            .Track("PART VOCALS").Note(0, 60)
            .Build());
        var harmoniesWithLyrics = DrumsAndBass();
        File.WriteAllBytes(Path.Combine(harmoniesWithLyrics.FullPath, "notes.mid"), new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART DRUMS").Note(0, 96)
            .Track("HARM1").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build());
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), lyricLessVocals, harmoniesWithLyrics], TestContext.CancellationToken);
        var staged = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0, ["vocals"] = 1, ["harmonies"] = 2 });
        var pending = staged with { SourceOffsets = new Dictionary<int, int> { [1] = 250, [2] = 250 } };

        var shifts = SongMerger.PreviewShifts(staged, pending);
        Assert.IsNotNull(shifts);
        Assert.AreEqual(250, shifts.VocalsMs);
    }

    [TestMethod]
    public async Task PartsAnOffsetWouldMoveBeforeTheStartAreFound()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 });

        Assert.IsEmpty(SongMerger.PartsBeforeStart(plan));
        Assert.IsEmpty(SongMerger.PartsBeforeStart(plan with { SourceOffsets = new Dictionary<int, int> { [1] = 300 } }));
        Assert.AreSequenceEqual(["drums", "guitar"], [.. SongMerger.PartsBeforeStart(plan with { SourceOffsets = new Dictionary<int, int> { [1] = -1 } })]);

        // Moving the base later moves the start with it, so the others' notes come before it.
        Assert.AreSequenceEqual(["drums", "guitar"], [.. SongMerger.PartsBeforeStart(plan with { SourceOffsets = new Dictionary<int, int> { [0] = 100 } })]);

        // Read once, the starts tell the same for any offsets, the plan's own aside.
        var starts = SongMerger.PartStartsOf(plan with { SourceOffsets = new Dictionary<int, int> { [1] = -1 } });
        Assert.IsEmpty(starts.BeforeStart(null));
        Assert.AreSequenceEqual(["drums", "guitar"], [.. starts.BeforeStart(new Dictionary<int, int> { [0] = 100 })]);
    }

    [TestMethod]
    public async Task TheBaseChartMovesOnlyAsFarAsTheDelayCanTakeIt()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["bass"] = 0, ["drums"] = 1, ["guitar"] = 1 }) with
        {
            SourceOffsets = new Dictionary<int, int> { [0] = 100_000_000, [1] = 99_999_999 },
        };

        // The delay stops at its furthest; the base chart's parts are placed against that, so the other song's are long after it.
        Assert.AreEqual("86399999", SongMerger.BuildMetadata(plan).Single(p => p.Key == "delay").Value);
        Assert.IsEmpty(SongMerger.PartsBeforeStart(plan));
    }

    [TestMethod]
    public async Task AVideoOffsetWithoutAVideoIsLeftOut()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { VideoOffset = 300 };
        string output = _root.CreateSubdirectory("out").FullName;

        var result = await SongMerger.MergeAsync(plan, output, OverwritePolicy.Skip, new NoVideo(), TestContext.CancellationToken);

        Assert.DoesNotContain(p => p.Key == "video_start_time", SngReader.Read(result.OutputPath, TestContext.CancellationToken).Metadata);
    }

    [TestMethod]
    public async Task StagingAgainReplacesOnlyWhatChanged()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var plan = Plan(analysis, new Dictionary<string, int> { ["drums"] = 0 }) with { AlbumArtSource = 0 };
        string folder = _root.CreateSubdirectory("preview").FullName;
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await SongMerger.StageAsync(plan, folder, new NoVideo(), staged, TestContext.CancellationToken);
        Assert.AreSequenceEqual(["notes.mid", "song.ini", "song.ogg", "album.png"], Names(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);

        // A file that would come from the same place isn't copied again.
        File.WriteAllBytes(Path.Combine(folder, "album.png"), [1, 2, 3]);
        await SongMerger.StageAsync(plan with { AudioSource = 1 }, folder, new NoVideo(), staged, TestContext.CancellationToken);

        Assert.AreSequenceEqual(["notes.mid", "song.ini", "guitar.ogg", "drums.ogg", "album.png"], Names(folder), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(folder, "album.png")));
        Assert.Contains("song_length = ", File.ReadAllText(Path.Combine(folder, "song.ini")));

        await SongMerger.StageAsync(plan with { AlbumArtSource = null }, folder, new NoVideo(), staged, TestContext.CancellationToken);
        Assert.IsFalse(File.Exists(Path.Combine(folder, "album.png")));
    }

    [TestMethod]
    public async Task SustainCutoffComesFromTheMergedChart()
    {
        // The .chart's guitar has a sustain of a third of a beat; the base's song.ini has a cutoff of its own.
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(sustainCutoff: 240), await DrumsAndGuitarSngAsync(guitarSustain: 64)], TestContext.CancellationToken);
        string folder = _root.CreateSubdirectory("preview").FullName;

        var warnings = await SongMerger.StageAsync(Plan(analysis, new Dictionary<string, int> { ["guitar"] = 1, ["bass"] = 0 }), folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        string ini = File.ReadAllText(Path.Combine(folder, "song.ini"));

        Assert.IsEmpty(warnings);
        // 64 ticks at 192 is 160 at the base's 480.
        Assert.Contains("sustain_cutoff_threshold = 159", ini);
        Assert.DoesNotContain("sustain_cutoff_threshold = 240", ini);

        // Without a sustain that short, the default is left to the game.
        var plain = await SongMergeAnalysis.LoadAsync([DrumsAndBass(sustainCutoff: 240), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        await SongMerger.StageAsync(Plan(plain, new Dictionary<string, int> { ["guitar"] = 1, ["bass"] = 0 }), folder, new NoVideo(), new Dictionary<string, string>(), TestContext.CancellationToken);
        Assert.DoesNotContain("sustain_cutoff_threshold", File.ReadAllText(Path.Combine(folder, "song.ini")));
    }

    [TestMethod]
    public async Task ConvertedDrumsKeepTheirLayoutInTheMergedSongIni()
    {
        // A MIDI chart's drums are pro unless song.ini says not; a .chart's without cymbal markers aren't.
        var plain = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync(cymbal: false)], TestContext.CancellationToken);
        var pro = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
        var parts = new Dictionary<string, int> { ["drums"] = 1, ["bass"] = 0 };

        var plainMetadata = SongMerger.BuildMetadata(Plan(plain, parts)).ToDictionary(p => p.Key, p => p.Value);
        var proMetadata = SongMerger.BuildMetadata(Plan(pro, parts)).ToDictionary(p => p.Key, p => p.Value);

        Assert.AreEqual("False", plainMetadata["pro_drums"]);
        Assert.AreEqual("True", proMetadata["pro_drums"]);
        Assert.IsFalse(proMetadata.ContainsKey("five_lane_drums"));
    }

    [TestMethod]
    public async Task PlanThatNamesAPartTheSourceLacksIsRejected()
    {
        var analysis = await SongMergeAnalysis.LoadAsync([DrumsAndBass(), await DrumsAndGuitarSngAsync()], TestContext.CancellationToken);
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
    private SongEntry DrumsAndBass(string genre = "Rock", int? sustainCutoff = null)
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
            {(sustainCutoff is { } cutoff ? $"sustain_cutoff_threshold = {cutoff}" : "")}
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
    private async Task<SongEntry> DrumsAndGuitarSngAsync(string name = "B", int guitarSustain = 0, bool cymbal = true)
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
        File.WriteAllText(Path.Combine(folder.FullName, "notes.chart"), $$"""
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
              0 = N 0 {{guitarSustain}}
              192 = N 1 0
            }
            [ExpertDrums]
            {
              0 = N 0 0
              96 = N 2 0
              {{(cymbal ? "96 = N 66 0" : "")}}
            }
            """);
        File.Copy(TestAudio.Fixture(Ogg), Path.Combine(folder.FullName, "guitar.ogg"));
        File.Copy(TestAudio.Fixture(Ogg), Path.Combine(folder.FullName, "drums.ogg"));

        var packs = _root.CreateSubdirectory("packs " + Guid.NewGuid().ToString("N")[..6]);
        string path = Path.Combine(packs.FullName, name + ".sng");
        await Packer.PackAsync(await PackPlanner.PlanAsync(folder.FullName, cancellationToken: TestContext.CancellationToken), path, overwrite: false, cancellationToken: TestContext.CancellationToken);
        return new SongEntry(SongEntryKind.SngFile, path, name + ".sng", name);
    }

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);

    private sealed class NoVideo : IVideoDurationProbe
    {
        public TimeSpan? Duration(Stream stream, string name) => null;
    }

    public TestContext TestContext { get; set; }
}
