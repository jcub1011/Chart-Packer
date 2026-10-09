using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Recovery;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartBuilder.Tests.Charting.Saving;
using static ChartBuilder.Tests.Charting.ChartSettingsCommandTests;
using static ChartBuilder.Tests.Charting.Saving.SaveFolders;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// The song's own edits as undo steps (charter-spec C1, Chart settings and Art): <c>song.ini</c> values, the art,
/// backgrounds and tracks, undone and redone as the chart's edits are, also once the song has been saved since.
/// </summary>
[TestClass]
public sealed class SongEditCommandsTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;

    private SaveFolders _folders = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void CreateFolders() => _folders = new SaveFolders();

    [TestCleanup]
    public void DeleteFolders() => _folders.Dispose();

    // song.ini values.

    [TestMethod]
    public void ASetValueIsUndoneAndRedone()
    {
        var song = Opened([KeyValuePair.Create("name", "Old")]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(SongCommands.SetValue(song, "name", "New")!);
        Assert.AreEqual("New", song.EffectiveValue("name"));
        Assert.IsTrue(song.HasUserChanges);

        undo.Undo();
        Assert.AreEqual("Old", song.EffectiveValue("name"));
        Assert.IsFalse(song.HasUserChanges, "Undone back to the song as it was, nothing is pending.");

        undo.Redo();
        Assert.AreEqual("New", song.EffectiveValue("name"));
    }

    [TestMethod]
    public void ARemoveAndARevertToSavedAreEachOneStep()
    {
        var song = Opened([KeyValuePair.Create("album", "Gone")]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(SongCommands.Remove(song, "album")!);
        Assert.IsNull(song.EffectiveValue("album"));
        undo.Execute(SongCommands.RevertToSaved(song, "album")!);
        Assert.AreEqual("Gone", song.EffectiveValue("album"));

        undo.Undo();
        Assert.IsNull(song.EffectiveValue("album"));
    }

    [TestMethod]
    public void AnEditThatChangesNothingIsNoCommand()
    {
        var song = Opened([KeyValuePair.Create("name", "Same")]);

        Assert.IsNull(SongCommands.SetValue(song, "name", "Same"));
        Assert.IsNull(SongCommands.Remove(song, "never_there"));
        Assert.IsNull(SongCommands.RevertToSaved(song, "name"));
    }

    [TestMethod]
    public void ClearingADifficultyUnratesIt()
    {
        var song = Opened([KeyValuePair.Create("diff_guitar", "2")]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(SongCommands.SetDifficulty(song, "diff_guitar", 5)!);
        undo.Execute(SongCommands.SetDifficulty(song, "diff_guitar", null)!);
        Assert.AreEqual(MetadataDifficulty.UnsetValue, song.EffectiveValue("diff_guitar"));

        undo.Undo();
        Assert.AreEqual("5", song.EffectiveValue("diff_guitar"));
        undo.Undo();
        Assert.AreEqual("2", song.EffectiveValue("diff_guitar"));
    }

    [TestMethod]
    public void AnAddedPartsRatingStillGoesWithThePart()
    {
        var song = Opened([]);
        var undo = new ChartUndoStack(song.Chart);
        undo.Execute(new AddPart(ChartInstrument.Bass));
        Assert.AreEqual(MetadataDifficulty.UnsetValue, song.EffectiveValue("diff_bass"));

        undo.Execute(SongCommands.SetDifficulty(song, "diff_bass", 4)!);
        undo.Undo();
        Assert.AreEqual(MetadataDifficulty.UnsetValue, song.EffectiveValue("diff_bass"));
        Assert.IsFalse(song.Edits.Any(e => e.Key == "diff_bass"), "Back to the rating the part calls for, there's no edit.");

        undo.Undo();
        Assert.IsNull(song.EffectiveValue("diff_bass"));
    }

    [TestMethod]
    public void ADelayOfZeroRemovesDelaySecondsAndUndoPutsItBack()
    {
        var song = Opened([KeyValuePair.Create("delay_seconds", "1.5")]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(SongCommands.SetDelay(song, 0)!);
        Assert.AreEqual("0", song.EffectiveValue("delay"));
        Assert.IsNull(song.EffectiveValue("delay_seconds"));

        undo.Undo();
        Assert.IsNull(song.EffectiveValue("delay"));
        Assert.AreEqual("1.5", song.EffectiveValue("delay_seconds"));
    }

    [TestMethod]
    public void AnEditTakesThePlaceOfTheDelayFindFirstDownbeatSetAndUndoPutsItBack()
    {
        var song = Opened([KeyValuePair.Create("delay", "100")]);
        var undo = new ChartUndoStack(song.Chart);
        song.DelayMs = 250;

        undo.Execute(SongCommands.SetDelay(song, 300)!);
        Assert.IsNull(song.DelayMs);
        Assert.AreEqual("300", song.EffectiveValue("delay"));

        undo.Undo();
        Assert.AreEqual("250", song.EffectiveValue("delay"));
    }

    [TestMethod]
    public async Task AnEditUndoneAfterASaveIsPendingAgain()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Old")));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        var undo = new ChartUndoStack(song.Chart);
        undo.Execute(SongCommands.SetValue(song, "name", "New")!);

        await SaveAsync(song, Token);
        Assert.IsFalse(song.HasUserChanges);

        undo.Undo();
        Assert.AreEqual("Old", song.EffectiveValue("name"));
        Assert.AreEqual(new PendingSongIniChange("name", "Old", PendingChangeReason.UserEdit), song.PendingChanges().Single(c => c.Key == "name"));

        undo.Redo();
        Assert.IsFalse(song.HasUserChanges, "Redone, the song is as it was saved.");
    }

    // Files.

    [TestMethod]
    public void TheAlbumArtIsSetRemovedAndRevertedAsSteps()
    {
        var song = Opened([], ["notes.mid", "album.jpg"]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(MediaCommands.Set(song, MediaSlot.AlbumArt, @"C:\Pictures\cover.png")!);
        Assert.AreEqual(new AlbumArtChange(@"C:\Pictures\cover.png"), song.AlbumArt);
        undo.Execute(MediaCommands.Remove(song, MediaSlot.AlbumArt)!);
        Assert.AreEqual(MediaRef.None, song.EffectiveMedia(MediaSlot.AlbumArt));
        undo.Execute(MediaCommands.RevertToSaved(song, MediaSlot.AlbumArt)!);
        Assert.IsNull(song.AlbumArt);
        Assert.IsFalse(song.HasUserChanges);

        undo.Undo();
        Assert.AreEqual(new AlbumArtChange(null), song.AlbumArt);
        undo.Undo();
        undo.Undo();
        Assert.AreEqual(MediaRef.Stored(0), song.EffectiveMedia(MediaSlot.AlbumArt));
        Assert.IsNull(MediaCommands.RevertToSaved(song, MediaSlot.AlbumArt));
    }

    [TestMethod]
    public void AFileOfTheWrongKindIsRefused() =>
        Assert.ThrowsExactly<ArgumentException>(() => MediaCommands.Set(Opened([]), MediaSlot.BackgroundVideo, @"C:\Pictures\cover.png"));

    [TestMethod]
    public void ATrackReplacedOrRemovedChangesTheTracks()
    {
        var song = Opened([], ["notes.mid", "song.ogg", "guitar.mp3"]);
        var undo = new ChartUndoStack(song.Chart);
        int changed = 0;
        song.TracksChanged += (_, _) => changed++;

        undo.Execute(MediaCommands.Set(song, MediaSlot.Track("guitar"), @"C:\Audio\lead.ogg")!);
        Assert.AreSequenceEqual(
            [new SongTrack("song.ogg", "song", null), new SongTrack("guitar.ogg", "guitar", @"C:\Audio\lead.ogg")],
            song.Tracks.ToList());
        undo.Execute(MediaCommands.Remove(song, MediaSlot.Track("song"))!);
        Assert.AreSequenceEqual(["guitar.ogg"], song.Tracks.Select(t => t.StoredName).ToList());

        undo.Undo();
        undo.Undo();
        Assert.AreSequenceEqual(["song.ogg", "guitar.mp3"], song.Tracks.Select(t => t.StoredName).ToList());
        Assert.AreEqual(4, changed);
    }

    [TestMethod]
    public void ANewChartsTracksStayStaged()
    {
        var song = EditorSong.New(NewChart(Guitar), [new SongTrack("song.ogg", "song", @"C:\Audio\mix.ogg")]);
        var undo = new ChartUndoStack(song.Chart);

        undo.Execute(MediaCommands.Set(song, MediaSlot.Track("drums"), @"C:\Audio\drums.wav")!);
        undo.Execute(MediaCommands.Remove(song, MediaSlot.Track("song"))!);
        Assert.AreSequenceEqual([new SongTrack("drums.wav", "drums", @"C:\Audio\drums.wav")], song.Tracks.ToList());

        undo.Execute(MediaCommands.RevertToSaved(song, MediaSlot.Track("song"))!);
        Assert.AreSequenceEqual(["song.ogg", "drums.wav"], song.Tracks.Select(t => t.StoredName).ToList());
        Assert.IsTrue(song.Tracks.All(t => t.IsStaged));
    }

    [TestMethod]
    public void TheDraftChangesTheBackgroundsAndReplacesAStemInAnyFormat()
    {
        var song = Opened([], ["notes.mid", "guitar.mp3", "background.jpg"]);
        var undo = new ChartUndoStack(song.Chart);
        undo.Execute(MediaCommands.Set(song, MediaSlot.Track("guitar"), @"C:\Audio\lead.ogg")!);
        undo.Execute(MediaCommands.Remove(song, MediaSlot.BackgroundImage)!);
        undo.Execute(MediaCommands.Set(song, MediaSlot.BackgroundVideo, @"C:\Video\clip.mp4")!);

        var draft = ChartSaver.DraftFor(ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges()), @"C:\staged\notes.mid", 1);

        Assert.IsTrue(draft.IsFileAdded("guitar.ogg"));
        Assert.IsTrue(draft.IsFileRemoved("guitar.mp3"));
        Assert.AreEqual(new BackgroundChange(null), draft.BackgroundImage);
        Assert.AreEqual(new BackgroundChange(@"C:\Video\clip.mp4"), draft.BackgroundVideo);
    }

    [TestMethod]
    [DataRow(SongEntryKind.SongFolder)]
    [DataRow(SongEntryKind.SngFile)]
    public async Task AFileReplacedBySaveIsBroughtBackByUndo(SongEntryKind kind)
    {
        byte[] oldArt = [1, 2, 3];
        byte[] oldGuitar = Wav(100);
        string path = await SongOfKindAsync(kind, ("album.jpg", oldArt), ("guitar.ogg", oldGuitar));
        string cover = _folders.Loose("cover.png", [9, 8, 7]);
        string lead = _folders.Loose("lead.wav", Wav(200));
        using var stash = MediaStash.Create();
        var song = EditorSong.Of(await OpenAsync(path, Token));
        var undo = new ChartUndoStack(song.Chart);
        undo.Execute(MediaCommands.Set(song, MediaSlot.AlbumArt, cover)!);
        undo.Execute(MediaCommands.Set(song, MediaSlot.Track("guitar"), lead)!);

        await SaveAsync(song, Token, stash: stash);
        var entry = SongOpener.EntryOf(path)!;
        Assert.AreSequenceEqual(new byte[] { 9, 8, 7 }, await ReadAsync(entry, "album.png", Token));
        Assert.Contains("guitar.wav", song.Opened!.StoredNames);
        Assert.DoesNotContain("guitar.ogg", song.Opened.StoredNames);

        undo.Undo();
        undo.Undo();
        Assert.IsTrue(song.HasUserChanges, "The files the save replaced are pending again.");
        Assert.AreEqual("guitar.ogg", song.Tracks.Single(t => t.Stem == "guitar").StoredName);
        await SaveAsync(song, Token, stash: stash);

        Assert.AreSequenceEqual(oldArt, await ReadAsync(entry, "album.jpg", Token));
        Assert.AreSequenceEqual(oldGuitar, await ReadAsync(entry, "guitar.ogg", Token));
        var reopened = await OpenAsync(path, Token);
        Assert.DoesNotContain("album.png", reopened.StoredNames);
        Assert.DoesNotContain("guitar.wav", reopened.StoredNames);

        undo.Redo();
        Assert.AreEqual(new AlbumArtChange(cover), song.AlbumArt);
    }

    [TestMethod]
    public async Task ANewChartsFirstSaveWritesItsBackgroundsAndTracksAsEdited()
    {
        string mix = _folders.Loose("mix.wav", Wav(100));
        string drums = _folders.Loose("kit.wav", Wav(100));
        string image = _folders.Loose("bg.png", [5, 6, 7]);
        var song = EditorSong.New(NewChart(Guitar), NewChartStems.TracksOf([mix]).Tracks);
        var undo = new ChartUndoStack(song.Chart);
        song.SetValue("name", "Song");
        undo.Execute(MediaCommands.Set(song, MediaSlot.Track("drums"), drums)!);
        undo.Execute(MediaCommands.Remove(song, MediaSlot.Track("song"))!);
        undo.Execute(MediaCommands.Set(song, MediaSlot.BackgroundImage, image)!);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());
        var stems = song.Tracks.Select(t => new NewChartStem(t.StagedSourcePath!, t.Stem)).ToList();

        string folder = await ChartSaver.SaveNewAsync(new NewSongTarget(_folders.Root, "New"), request, ChartSaver.Check(request), stems, Token);

        Assert.IsTrue(File.Exists(Path.Combine(folder, "drums.wav")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, "song.wav")));
        Assert.AreSequenceEqual(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(Path.Combine(folder, "background.png"), Token));

        var entry = SongOpener.EntryOf(folder)!;
        song.Saved(ChartSaver.SavedSong(song, entry, request), song.Chart.Parts.Select(p => p.Instrument).ToHashSet());
        Assert.AreSequenceEqual([new SongTrack("drums.wav", "drums", null)], song.Tracks.ToList());
    }

    [TestMethod]
    public async Task FileChangesAreRecovered()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")), ("song.ogg", [1]));
        var store = new RecoveryStore(Path.Combine(_folders.Root, "Recovery"));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        var undo = new ChartUndoStack(song.Chart);
        undo.Execute(MediaCommands.Set(song, MediaSlot.BackgroundVideo, @"C:\Video\clip.webm")!);
        undo.Execute(MediaCommands.Remove(song, MediaSlot.Track("song"))!);
        undo.Execute(MediaCommands.Set(song, MediaSlot.AlbumArt, @"C:\Pictures\cover.jpg")!);
        await store.WriteAsync(RecoveryStore.KeyOf(folder), MidiChartWriter.Write(song.Chart), Autosave.ManifestFor(song, song.Title ?? "", DateTimeOffset.Now), Token);

        var recovered = Autosave.Recover(await OpenAsync(folder, Token), store.Find(folder)!);

        CollectionAssert.AreEquivalent(song.Media.ToList(), recovered.Media.ToList());
        Assert.IsEmpty(recovered.Tracks);
    }

    private async Task<string> SongOfKindAsync(SongEntryKind kind, params (string Name, byte[] Bytes)[] files)
    {
        (string, byte[])[] all = [("notes.mid", MidiFixtures.LeadGuitar()), .. files];
        if (kind == SongEntryKind.SongFolder)
            return _folders.Song("Song", [("song.ini", Ini("name = Song")), .. all]);

        string sng = Path.Combine(_folders.Root, "Song.sng");
        var sources = all.Select(f => new SngSourceFile(f.Item1, _folders.Loose("src-" + f.Item1, f.Item2), f.Item2.Length)).ToList();
        await SngWriter.WriteAsync(sng, SngMask.Generate(), [KeyValuePair.Create("name", "Song")], sources, overwrite: false, cancellationToken: Token);
        return sng;
    }

    private static EditorSong Opened(IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<string>? storedNames = null) => EditorSong.Of(new OpenedSong(
        new SongEntry(SongEntryKind.SongFolder, @"C:\Song", ".", "Song"),
        pairs,
        storedNames ?? ["notes.mid"],
        "notes.mid",
        new ChartLoadResult(NewChart(Guitar), []),
        ChartReadOptions.From(pairs),
        SongChartSettings.DelayOf(pairs, (TimeSpan?)null),
        null,
        new ChartFileStamp("notes.mid", 1, DateTime.UnixEpoch)));
}
