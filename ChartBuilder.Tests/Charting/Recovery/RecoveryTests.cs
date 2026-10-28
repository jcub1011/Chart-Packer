using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Recovery;
using ChartShared.Core.Charts;
using ChartShared.Core.Tests.Isolation;
using ChartBuilder.Tests.Charting.Saving;
using static ChartBuilder.Tests.Charting.Saving.SaveFolders;

namespace ChartBuilder.Tests.Charting.Recovery;

/// <summary>Autosave and recovery (charter-spec C15 item 6, C2 item 7), in a recovery folder of the test's own.</summary>
[TestClass]
public sealed class RecoveryTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    private SaveFolders _folders = null!;
    private RecoveryStore _store = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void CreateFolders()
    {
        _folders = new SaveFolders();
        _store = new RecoveryStore(Path.Combine(_folders.Root, "Recovery"));
    }

    [TestCleanup]
    public void DeleteFolders() => _folders.Dispose();

    [TestMethod]
    public async Task KillingTheAppMidEditOffersTheChartAsOfTheLastAutosave()
    {
        // AC C15.2.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")));
        var before = FolderSnapshot.Take(folder);
        var opened = await OpenAsync(folder, Token);
        var song = EditorSong.Of(opened);
        song.DefaultCharter = "Me";
        song.Revert("charter");
        var stack = new ChartUndoStack(song.Chart);
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(96_000, 1, 2)]));
        stack.Execute(new AddPart(ChartInstrument.Bass));
        var atAutosave = song.Chart.Clone();
        await AutosaveAsync(song);

        // Edits after the last autosave are lost with the process.
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(97_000, 1, 3)]));

        var reopened = await OpenAsync(folder, Token);
        var file = _store.Find(folder)!;
        Assert.AreEqual(RecoveryOffer.Offer, await RecoveryStore.AssessAsync(file, reopened.Entry, Token));
        var recovered = Autosave.Recover(reopened, file);

        var differences = ChartModelComparer.Differences(MidiChartWriter.Normalize(atAutosave), recovered.Chart);
        Assert.IsEmpty(differences, string.Join("; ", differences.Select(d => d.Description)));
        Assert.Contains("charter", recovered.Reverted, "Reverted changes stay reverted.");
        Assert.AreSequenceEqual(
            new[] { "diff_bass", "sustain_cutoff_threshold" },
            [.. recovered.PendingChanges().Select(c => c.Key)],
            "The part added before the autosave still needs its rating, and the chart derives its cutoff.");
        Assert.IsEmpty(before.DiffTo(FolderSnapshot.Take(folder)), "Autosave never writes into the song.");
    }

    [TestMethod]
    public async Task ASongChangedOnDiskSinceItsAutosaveIsStillOfferedButFlagged()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        new AddNotes(Guitar, Expert, [new EditableNote(96_000, 1, 2)]).Apply(song.Chart);
        await AutosaveAsync(song);

        await File.WriteAllBytesAsync(Path.Combine(folder, "notes.mid"), MidiFixtures.ForcingAndTaps(), Token);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "notes.mid"), DateTime.UtcNow.AddMinutes(5));

        Assert.AreEqual(RecoveryOffer.OfferChanged, await RecoveryStore.AssessAsync(_store.Find(folder)!, SongOpener.EntryOf(folder)!, Token));
    }

    [TestMethod]
    public async Task AnAutosaveThatIsTheSongsChartIsStale()
    {
        // A save that went through, then a crash before its recovery file was deleted.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        await AutosaveAsync(song);
        File.Copy(_store.Find(folder)!.ChartPath, Path.Combine(folder, "notes.mid"), overwrite: true);

        Assert.AreEqual(RecoveryOffer.Stale, await RecoveryStore.AssessAsync(_store.Find(folder)!, SongOpener.EntryOf(folder)!, Token));
    }

    [TestMethod]
    public async Task AnAutosaveCutShortIsIgnored()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        await AutosaveAsync(song);
        string chart = _store.Find(folder)!.ChartPath;

        // The chart replaced by a newer one whose manifest was never written.
        await File.WriteAllBytesAsync(chart, MidiFixtures.ForcingAndTaps(), Token);
        Assert.IsNull(_store.Find(folder));

        // A manifest with no chart.
        File.Delete(chart);
        Assert.IsNull(_store.Find(folder));
    }

    [TestMethod]
    public async Task ACrashBeforeANewAutosavesManifestKeepsTheLastOne()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        await AutosaveAsync(song);
        var last = _store.Find(folder)!;

        // A newer chart written beside it, whose manifest never was.
        File.WriteAllBytes(Path.Combine(_store.Folder, RecoveryStore.KeyOf(folder) + "-0123456789abcdef.mid"), MidiFixtures.ForcingAndTaps());

        var found = _store.Find(folder)!;
        Assert.AreEqual(last.ChartPath, found.ChartPath);
        await AutosaveAsync(song);
        _store.Delete(RecoveryStore.KeyOf(folder));
        Assert.IsEmpty(Directory.GetFiles(_store.Folder), "Delete takes every chart of the key.");
    }

    [TestMethod]
    public async Task AnUntitledChartIsRecoveredWithItsAudioAndItsName()
    {
        string stem = _folders.Loose("Guitar.ogg", [1, 2, 3]);
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
        var song = EditorSong.New(chart, NewChartStems.TracksOf([stem]).Tracks);
        song.Values = [KeyValuePair.Create("name", "Draft")];
        new AddNotes(Guitar, Expert, [new EditableNote(0, 1, 0)]).Apply(song.Chart);
        string key = RecoveryStore.KeyOfNew(Guid.NewGuid());
        await _store.WriteAsync(key, MidiChartWriter.Write(song.Chart), Autosave.ManifestFor(song, "Untitled chart", DateTimeOffset.Now), Token);

        var file = _store.NewCharts().Single();
        var recovered = Autosave.RecoverNew(file);

        Assert.AreEqual(key, file.Key);
        Assert.IsTrue(recovered.IsNew);
        Assert.AreEqual(stem, recovered.Tracks.Single().StagedSourcePath);
        Assert.AreEqual("guitar", recovered.Tracks.Single().Stem);
        Assert.AreEqual("Draft", recovered.Values.Single().Value);
        Assert.AreEqual(1, recovered.Chart.PartOf(Guitar)![Expert].Notes.Count);

        _store.Delete(key);
        Assert.IsEmpty(_store.NewCharts());
    }

    [TestMethod]
    [DataRow(false, false, 3, -1, false, AutosaveAction.Nothing)]
    [DataRow(false, true, 3, -1, false, AutosaveAction.Delete)]
    [DataRow(true, false, 3, 3, false, AutosaveAction.Write)]
    [DataRow(true, true, 3, 3, false, AutosaveAction.Nothing)]
    [DataRow(true, true, 4, 3, false, AutosaveAction.Write)]
    [DataRow(true, true, 4, 3, true, AutosaveAction.Later)]
    public void AnAutosaveWritesOnlyWhatChangedSinceTheLast(bool dirty, bool hasFile, int version, int autosaved, bool busy, AutosaveAction expected)
    {
        Assert.AreEqual(expected, Autosave.Next(dirty, hasFile, version, autosaved, busy));
    }

    [TestMethod]
    public async Task TheDelayFindFirstDownbeatSetIsRecovered()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song", "delay = 300")));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        song.DelayMs = 1250;
        await AutosaveAsync(song);

        var recovered = Autosave.Recover(await OpenAsync(folder, Token), _store.Find(folder)!);

        Assert.AreEqual(1250, recovered.DelayMs);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1250), recovered.Delay);
    }

    [TestMethod]
    public async Task ASuggestedRatingUsedIsRecovered()
    {
        // charter-spec C18 item 3: a rating used is a pending change, kept by autosave until it's saved.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song", "diff_guitar = -1")));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        song.SetRating("diff_guitar", 4);
        await AutosaveAsync(song);

        var recovered = Autosave.Recover(await OpenAsync(folder, Token), _store.Find(folder)!);

        Assert.AreEqual(4, recovered.Ratings["diff_guitar"]);
        Assert.Contains(c => c.Key == "diff_guitar" && c.Value == "4", recovered.PendingChanges());
    }

    private Task AutosaveAsync(EditorSong song) =>
        _store.WriteAsync(RecoveryStore.KeyOf(song.Entry!.FullPath), MidiChartWriter.Write(song.Chart), Autosave.ManifestFor(song, song.Title ?? "", DateTimeOffset.Now), Token);
}
