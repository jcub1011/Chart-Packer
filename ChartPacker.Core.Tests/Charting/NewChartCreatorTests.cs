using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Audio;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartCreatorTests
{
    private const int SampleRate = 8000;

    private DirectoryInfo _root = null!;
    private string _sources = null!;
    private string _library = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-newchart-");
        _sources = Directory.CreateDirectory(Path.Combine(_root.FullName, "sources")).FullName;
        _library = Directory.CreateDirectory(Path.Combine(_root.FullName, "library")).FullName;
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private static readonly NewChartSettings Settings =
        new("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro);

    private NewSongFolder Request(params NewChartStem[] stems) => Request(Settings, "", stems);

    /// <summary>A new chart's folder as its first save gives it: the new chart, and the song.ini its pending changes make.</summary>
    private NewSongFolder Request(NewChartSettings settings, string charter, params NewChartStem[] stems)
    {
        var song = EditorSong.New(EditableChart.CreateNew(settings), []);
        song.Values = [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", "Artist")];
        song.DefaultCharter = charter;
        song.AudioLengthMs = 1000;
        return new NewSongFolder
        {
            ParentFolder = _library,
            FolderName = "Artist - Song",
            NotesMid = MidiChartWriter.Write(song.Chart),
            SongIni = song.EffectivePairs(),
            Stems = stems,
        };
    }

    [TestMethod]
    public async Task TheFolderHoldsTheStemsArtChartAndMetadata()
    {
        // AC C1.4: the files are stored as their stems, lowercased.
        var request = Request(
            new NewChartStem(Source("Guitar.OGG", "guitar"u8), "guitar"),
            new NewChartStem(Source("Drums.ogg", "drums"u8), "drums"),
            new NewChartStem(Source("mix.mp3", "mix"u8), "song")) with
        {
            AlbumArtPath = Source("Cover.PNG", "png"u8),
        };

        string folder = await NewChartCreator.CreateAsync(request, TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(_library, "Artist - Song"), folder);
        Assert.AreSequenceEqual(
            ["album.png", "drums.ogg", "guitar.ogg", "notes.mid", "song.ini", "song.mp3"],
            [.. Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)]);
        Assert.AreEqual("guitar", File.ReadAllText(Path.Combine(folder, "guitar.ogg")));
        Assert.AreEqual("png", File.ReadAllText(Path.Combine(folder, "album.png")));
        Assert.AreEqual(480, ChartReader.Read(File.ReadAllBytes(Path.Combine(folder, "notes.mid")), "notes.mid").Tempo.Resolution);

        var ini = await SongIni.ReadAsync(Path.Combine(folder, SongIni.FileName), TestContext.CancellationToken);
        CollectionAssert.Contains(ini.Pairs.ToList(), KeyValuePair.Create("name", "Song"));
        CollectionAssert.Contains(ini.Pairs.ToList(), KeyValuePair.Create("pro_drums", "True"));
        AssertNothingStaged();
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FiveLane)]
    public async Task ACreatedChartReopensWithItsTempoMapPartsAndMetadata(DrumsVariant drums)
    {
        // AC C1.1 and C1.5: it reopens in the editor as it was created, the drums read with their layout.
        var settings = new NewChartSettings("Song", 960, 145.5, 7, 8, new HashSet<ChartInstrument> { ChartInstrument.Bass, ChartInstrument.GhlGuitar, ChartInstrument.Drums }, drums);
        var request = Request(settings, "Me", new NewChartStem(Wav("mix.wav", seconds: 1), "song"));

        string folder = await NewChartCreator.CreateAsync(request, TestContext.CancellationToken);
        var opened = await SongOpener.OpenAsync(SongOpener.EntryOf(folder)!, TestContext.CancellationToken);

        Assert.IsNull(opened.Problem);
        var song = opened.Song!;
        var chart = song.Chart;
        Assert.AreEqual(960, chart.Resolution);
        Assert.AreSequenceEqual([new TempoChange(0, EditableTempoMap.MicrosecondsPerQuarterOf(145.5))], chart.TempoMap.Tempos.ToList());
        Assert.AreSequenceEqual([(0L, 7, 8)], chart.TempoMap.TimeSignatures.Select(s => (s.Tick, s.Numerator, s.Denominator)).ToList());
        Assert.AreSequenceEqual([ChartInstrument.Bass, ChartInstrument.Drums, ChartInstrument.GhlGuitar], chart.Parts.Select(p => p.Instrument).ToList());
        Assert.AreEqual(drums, chart.DrumsVariant);
        Assert.AreEqual("Song", song.Value("name"));
        Assert.AreEqual("Artist", song.Value("artist"));
        Assert.AreEqual("Me", song.Value("charter"));
        Assert.AreEqual("-1", song.Value("diff_bass"));
        Assert.IsEmpty(PendingSongIniChanges.Of(chart, chart.Parts.Select(p => p.Instrument).ToHashSet(), song.Pairs), "Nothing is left to write.");
        Assert.AreEqual(
            drums switch { DrumsVariant.FourLane => DrumsLayout.FourLane, DrumsVariant.FiveLane => DrumsLayout.FiveLane, _ => DrumsLayout.Pro },
            new DrumsLayouts(song.Options, midi: true).Layout);
    }

    [TestMethod]
    public async Task ASongWithOneStemValidatesAndPacks()
    {
        // AC C1.1.
        string folder = await NewChartCreator.CreateAsync(Request(new NewChartStem(Wav("mix.wav", seconds: 1), "song")), TestContext.CancellationToken);

        var plan = await PackPlanner.PlanAsync(folder, cancellationToken: TestContext.CancellationToken);
        Assert.IsFalse(plan.HasErrors, string.Join(Environment.NewLine, plan.Findings));
        Assert.IsFalse(plan.Findings.Any(f => f.RuleId == SngRules.Sng052));
        Assert.IsEmpty(MetadataValidator.Validate(plan.Metadata).Where(MetadataValidator.IsFailing));

        string sng = Path.Combine(_root.FullName, "Artist - Song.sng");
        var findings = await Packer.PackAsync(plan, sng, overwrite: false, cancellationToken: TestContext.CancellationToken);
        Assert.IsFalse(findings.Any(f => f.Severity == Severity.Error));
        Assert.IsTrue(SngReader.Read(sng, TestContext.CancellationToken).Files.Any(f => f.Name == "notes.mid"));
    }

    [TestMethod]
    public async Task AnExistingFolderIsNeverTouched()
    {
        // AC C1.3.
        string existing = Directory.CreateDirectory(Path.Combine(_library, "Artist - Song")).FullName;
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");

        await Assert.ThrowsAsync<IOException>(() => NewChartCreator.CreateAsync(Request(new NewChartStem(Wav("mix.wav", 1), "song")), TestContext.CancellationToken));

        Assert.AreSequenceEqual([Path.Combine(existing, "keep.txt")], Directory.GetFileSystemEntries(existing));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
        AssertNothingStaged();
    }

    [TestMethod]
    public async Task AnExistingFileWithTheNameIsNeverTouched()
    {
        string existing = Path.Combine(_library, "Artist - Song");
        File.WriteAllText(existing, "keep");

        await Assert.ThrowsAsync<IOException>(() => NewChartCreator.CreateAsync(Request(), TestContext.CancellationToken));

        Assert.AreEqual("keep", File.ReadAllText(existing));
        AssertNothingStaged();
    }

    [TestMethod]
    public async Task CancellingLeavesNothingBehind()
    {
        // AC C1.2.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => NewChartCreator.CreateAsync(Request(new NewChartStem(Wav("mix.wav", 1), "song")), cts.Token));

        Assert.IsEmpty(Directory.GetFileSystemEntries(_library));
    }

    [TestMethod]
    public async Task AStemThatCantBeCopiedLeavesNothingBehind()
    {
        // AC C1.2.
        var request = Request(
            new NewChartStem(Wav("mix.wav", 1), "song"),
            new NewChartStem(Path.Combine(_sources, "missing.ogg"), "guitar"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => NewChartCreator.CreateAsync(request, TestContext.CancellationToken));

        Assert.IsEmpty(Directory.GetFileSystemEntries(_library));
    }

    [TestMethod]
    public async Task AStemThatsInUseLeavesNothingBehind()
    {
        // AC C1.2.
        string locked = Wav("locked.wav", 1);
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAsync<IOException>(() => NewChartCreator.CreateAsync(Request(new NewChartStem(locked, "song")), TestContext.CancellationToken));
                Assert.IsEmpty(Directory.GetFileSystemEntries(_library));
            }
        }
    }

    [TestMethod]
    public async Task TwoFilesCantShareAStem()
    {
        var request = Request(new NewChartStem(Wav("a.wav", 1), "song"), new NewChartStem(Wav("b.wav", 1), "Song"));

        await Assert.ThrowsAsync<ArgumentException>(() => NewChartCreator.CreateAsync(request, TestContext.CancellationToken));

        Assert.IsEmpty(Directory.GetFileSystemEntries(_library));
    }

    [TestMethod]
    public async Task AFolderNameBreakingTheFilenameRulesIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewChartCreator.CreateAsync(Request() with { FolderName = "CON" }, TestContext.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => NewChartCreator.CreateAsync(Request() with { FolderName = "a/b" }, TestContext.CancellationToken));

        Assert.IsEmpty(Directory.GetFileSystemEntries(_library));
    }

    [TestMethod]
    public async Task AMissingParentFolderIsRefused()
    {
        var request = Request() with { ParentFolder = Path.Combine(_root.FullName, "nowhere") };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => NewChartCreator.CreateAsync(request, TestContext.CancellationToken));

        Assert.IsFalse(Directory.Exists(request.ParentFolder));
    }

    private string Source(string name, ReadOnlySpan<byte> bytes)
    {
        string path = Path.Combine(_sources, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Wav(string name, double seconds) =>
        Source(name, TestAudio.Wav((int)(seconds * SampleRate), SampleRate, 1, (_, _) => 0));

    private void AssertNothingStaged() =>
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(_library).Any(p => p.EndsWith(StagingPath.Suffix, StringComparison.Ordinal)));

    public TestContext TestContext { get; set; }
}
