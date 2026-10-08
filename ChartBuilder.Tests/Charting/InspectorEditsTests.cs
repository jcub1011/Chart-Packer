using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Recovery;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartBuilder.Tests.Charting.Saving;
using static ChartBuilder.Tests.Charting.ChartSettingsCommandTests;
using static ChartBuilder.Tests.Charting.Saving.SaveFolders;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// What the Chart Inspector edits (charter-spec C1, §4.5): <c>song.ini</c> keys and album art, each a pending change until
/// it's saved, taken back by Revert rather than undo.
/// </summary>
[TestClass]
public sealed class InspectorEditsTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;

    private SaveFolders _folders = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void CreateFolders() => _folders = new SaveFolders();

    [TestCleanup]
    public void DeleteFolders() => _folders.Dispose();

    // Pending changes.

    [TestMethod]
    public void AnEditReplacesTheSongsValue()
    {
        var song = Opened([KeyValuePair.Create("name", "Old"), KeyValuePair.Create("album", "Gone")]);

        song.SetValue("name", "New");
        song.RemoveValue("album");

        Assert.AreSequenceEqual(
            [new PendingSongIniChange("name", "New", PendingChangeReason.UserEdit), new PendingSongIniChange("album", null, PendingChangeReason.UserEdit)],
            song.PendingChanges().ToList());
        Assert.AreSequenceEqual(new[] { KeyValuePair.Create("name", "New") }, [.. song.EffectivePairs()]);
        Assert.IsTrue(song.PendingChanges().All(c => c.CanRevert));
    }

    [TestMethod]
    public void AnEditTakesThePlaceOfWhatChartBuilderWouldHaveSet()
    {
        var song = EditorSong.New(NewChart(Guitar), []);
        song.DefaultCharter = "Me";
        song.AudioLengthMs = 90_000;
        song.Values = [KeyValuePair.Create("name", "From tags")];

        song.SetValue("charter", "Someone else");
        song.SetValue("song_length", "12345");
        song.SetValue("name", "Typed");

        var changes = song.PendingChanges();
        Assert.AreEqual("Someone else", changes.Single(c => c.Key == "charter").Value);
        Assert.AreEqual("12345", changes.Single(c => c.Key == "song_length").Value);
        Assert.AreEqual("Typed", changes.Single(c => c.Key == "name").Value);
        Assert.IsTrue(changes.Where(c => c.Key is "charter" or "song_length" or "name").All(c => c.Reason == PendingChangeReason.UserEdit));
    }

    [TestMethod]
    public void AnEditThatLeavesTheSongAsItWasListsNothing()
    {
        var song = Opened([KeyValuePair.Create("name", "Same"), KeyValuePair.Create("song_length", "1000")]);
        song.AudioLengthMs = 90_000;
        Assert.IsTrue(song.PendingChanges().Any(c => c.Key == "song_length"));

        song.SetValue("name", "Same");
        song.SetValue("song_length", "1000");
        song.RemoveValue("never_there");

        Assert.IsEmpty(song.PendingChanges(), "Setting song_length back as it was keeps it so.");
    }

    [TestMethod]
    public void TheKeysTheChartDecidesCanNotBeEdited()
    {
        var chart = NewChart(Guitar);
        new AddPart(ChartInstrument.Drums, DrumsVariant.Pro).Apply(chart);
        var song = EditorSong.New(chart, []);

        song.SetValue("pro_drums", "False");
        song.SetValue("multiplier_note", "103");

        Assert.IsFalse(song.CanEdit("pro_drums"));
        Assert.IsFalse(song.CanEdit("multiplier_note"));
        Assert.IsTrue(song.CanEdit("name"));
        Assert.AreEqual("True", song.PendingChanges().Single(c => c.Key == "pro_drums").Value);
        Assert.IsFalse(song.PendingChanges().Any(c => c.Key == "multiplier_note"));
    }

    [TestMethod]
    public void RevertTakesAnEditBackRatherThanLeavingItStruckThrough()
    {
        var song = Opened([KeyValuePair.Create("name", "Old")]);
        song.SetValue("name", "New");

        song.Revert("name");

        Assert.IsEmpty(song.AllPendingChanges());
        Assert.IsEmpty(song.Reverted);
        Assert.IsEmpty(song.Edits);
    }

    [TestMethod]
    public void ARatingOrADelaySetAfterAnEditTakesItsPlaceAndTheOtherWayRound()
    {
        var chart = NewChart(Guitar);
        var song = EditorSong.Of(OpenedSong([KeyValuePair.Create("diff_guitar", "-1"), KeyValuePair.Create("delay", "100")], chart));

        song.SetValue("diff_guitar", "2");
        song.SetRating("diff_guitar", 5);
        Assert.AreEqual("5", song.PendingChanges().Single(c => c.Key == "diff_guitar").Value);
        song.SetValue("diff_guitar", "3");
        Assert.AreEqual("3", song.PendingChanges().Single(c => c.Key == "diff_guitar").Value);
        Assert.IsFalse(song.Ratings.ContainsKey("diff_guitar"));

        song.SetValue("delay", "200");
        song.DelayMs = 400;
        Assert.AreEqual(TimeSpan.FromMilliseconds(400), song.Delay);
        song.SetDelay(600);
        Assert.AreEqual(TimeSpan.FromMilliseconds(600), song.Delay);
        Assert.IsNull(song.DelayMs);
    }

    [TestMethod]
    public void ADelayOfNothingTypedRemovesDelaySecondsToo()
    {
        var song = Opened([KeyValuePair.Create("delay_seconds", "0.5")]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), song.Delay);

        song.SetDelay(0);

        Assert.AreEqual(TimeSpan.Zero, song.Delay);
        Assert.IsFalse(song.EffectivePairs().Any(p => p.Key == "delay_seconds"));
    }

    [TestMethod]
    public void TheTitleFollowsTheEditedNameAndArtist()
    {
        var song = Opened([KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", "Band")]);
        Assert.AreEqual("Band – Song", song.Title);

        song.SetValue("name", "Renamed");

        Assert.AreEqual("Band – Renamed", song.Title);
        Assert.IsNull(EditorSong.New(NewChart(Guitar), []).Title, "A new chart with neither is untitled.");
    }

    [TestMethod]
    public void UserChangesCountUntilSavedAndEachRaisesChanged()
    {
        var song = Opened([KeyValuePair.Create("name", "Old")]);
        int changed = 0;
        song.Changed += (_, _) => changed++;
        Assert.IsFalse(song.HasUserChanges);

        song.SetValue("name", "New");
        song.SetValue("name", "New");
        Assert.IsTrue(song.HasUserChanges);
        song.DiscardEdit("name");
        Assert.IsFalse(song.HasUserChanges);
        song.RemoveAlbumArt();
        Assert.IsTrue(song.HasUserChanges);
        song.RevertAlbumArt();
        Assert.IsFalse(song.HasUserChanges);

        Assert.AreEqual(4, changed, "Setting the same value again changes nothing.");
    }

    // Saving.

    [TestMethod]
    public async Task EditsAndAlbumArtAreSavedAndThenClearedAsTheSongMovesOn()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song", "album = Old")), ("album.jpg", [1, 2, 3]));
        string art = _folders.Loose("cover.PNG", [9, 8, 7]);
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        song.SetValue("artist", "Band");
        song.RemoveValue("album");
        song.SetAlbumArt(art);

        await SaveAsync(song, Token);

        var reopened = await OpenAsync(folder, Token);
        Assert.AreEqual("Band", reopened.Value("artist"));
        Assert.IsNull(reopened.Value("album"));
        Assert.IsFalse(File.Exists(Path.Combine(folder, "album.jpg")));
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(Path.Combine(folder, "album.png"), Token));
        Assert.IsFalse(song.HasUserChanges);
        Assert.IsEmpty(song.PendingChanges());
        Assert.Contains("album.png", song.Opened!.StoredNames);
        Assert.DoesNotContain("album.jpg", song.Opened.StoredNames);
    }

    [TestMethod]
    public void TheDraftRemovesAlbumArtWhenItIsRemoved()
    {
        var song = Opened([]);
        song.RemoveAlbumArt();

        var draft = ChartSaver.DraftFor(ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges()), @"C:\staged\notes.mid", 1);

        Assert.AreEqual(new AlbumArtChange(null), draft.AlbumArt);
    }

    [TestMethod]
    public async Task ANewChartsFirstSaveWritesItsAlbumArt()
    {
        string art = _folders.Loose("cover.jpeg", [4, 5, 6]);
        var song = EditorSong.New(NewChart(Guitar), []);
        song.SetValue("name", "Song");
        song.SetAlbumArt(art);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());

        string folder = await ChartSaver.SaveNewAsync(new NewSongTarget(_folders.Root, "New"), request, ChartSaver.Check(request), [], Token);

        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(folder, "album.jpeg"), Token));
        Assert.AreEqual("Song", (await OpenAsync(folder, Token)).Value("name"));
    }

    // Recovery.

    [TestMethod]
    public async Task EditsAndAlbumArtAreRecovered()
    {
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song", "album = Old")));
        var store = new RecoveryStore(Path.Combine(_folders.Root, "Recovery"));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        song.SetValue("name", "Renamed");
        song.RemoveValue("album");
        song.SetAlbumArt(@"C:\Pictures\cover.png");
        await store.WriteAsync(RecoveryStore.KeyOf(folder), MidiChartWriter.Write(song.Chart), Autosave.ManifestFor(song, song.Title ?? "", DateTimeOffset.Now), Token);

        var recovered = Autosave.Recover(await OpenAsync(folder, Token), store.Find(folder)!);

        Assert.AreSequenceEqual(song.Edits.ToList(), recovered.Edits.ToList());
        Assert.AreEqual(new AlbumArtChange(@"C:\Pictures\cover.png"), recovered.AlbumArt);
        Assert.AreEqual("Renamed", recovered.PendingChanges().Single(c => c.Key == "name").Value);
    }

    private static EditorSong Opened(IReadOnlyList<KeyValuePair<string, string>> pairs) => EditorSong.Of(OpenedSong(pairs, NewChart(Guitar)));

    private static OpenedSong OpenedSong(IReadOnlyList<KeyValuePair<string, string>> pairs, EditableChart chart) => new(
        new SongEntry(SongEntryKind.SongFolder, @"C:\Song", ".", "Song"),
        pairs,
        ["notes.mid"],
        "notes.mid",
        new ChartLoadResult(chart, []),
        ChartReadOptions.From(pairs),
        SongChartSettings.DelayOf(pairs, (TimeSpan?)null),
        null,
        new ChartFileStamp("notes.mid", 1, DateTime.UnixEpoch));
}
