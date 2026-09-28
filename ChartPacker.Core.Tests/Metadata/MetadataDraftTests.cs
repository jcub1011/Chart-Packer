using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class MetadataDraftTests
{
    [TestMethod]
    public void SetReplacesValueInPlaceKeepingStoredKeyCase()
    {
        var draft = new MetadataDraft();
        draft.Set("NAME", "New");

        var result = draft.ApplyTo([P("artist", "A"), P("Name", "Old"), P("album", "B")]);

        CollectionAssert.AreEqual(new[] { P("artist", "A"), P("Name", "New"), P("album", "B") }, result);
    }

    [TestMethod]
    public void SetCollapsesDuplicatesOfThatKey()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");

        var result = draft.ApplyTo([P("name", "One"), P("artist", "A"), P("name", "Two")]);

        CollectionAssert.AreEqual(new[] { P("name", "New"), P("artist", "A") }, result);
    }

    [TestMethod]
    public void RemoveDropsEveryDuplicate()
    {
        var draft = new MetadataDraft();
        draft.Remove("Name");

        var result = draft.ApplyTo([P("name", "One"), P("artist", "A"), P("NAME", "Two")]);

        CollectionAssert.AreEqual(new[] { P("artist", "A") }, result);
    }

    [TestMethod]
    public void MissingKeysAreAppendedInDraftOrder()
    {
        var draft = new MetadataDraft();
        draft.Set("year", "2001");
        draft.Set("genre", "Rock");

        var result = draft.ApplyTo([P("name", "Title")]);

        CollectionAssert.AreEqual(new[] { P("name", "Title"), P("year", "2001"), P("genre", "Rock") }, result);
    }

    [TestMethod]
    public void UneditedPairsAndTheirDuplicatesAreUntouched()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");
        var pairs = new[] { P("artist", "A"), P("artist", "B"), P("name", "Old"), P("icon", "x") };

        var result = draft.ApplyTo(pairs);

        CollectionAssert.AreEqual(new[] { P("artist", "A"), P("artist", "B"), P("name", "New"), P("icon", "x") }, result);
    }

    [TestMethod]
    public void RemovingAMissingKeyChangesNothing()
    {
        var draft = new MetadataDraft();
        draft.Remove("year");
        var pairs = new[] { P("name", "Title") };

        CollectionAssert.AreEqual(pairs, draft.ApplyTo(pairs));
    }

    [TestMethod]
    public void LaterChangeToSameKeyReplacesEarlierOne()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "First");
        draft.Remove("NAME");

        Assert.IsTrue(draft.TryGetChange("name", out var change));
        Assert.IsTrue(change.IsRemoval);
        Assert.HasCount(1, draft.Keys);
    }

    [TestMethod]
    public void RevertForgetsTheChange()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");
        draft.Set("artist", "B");

        draft.Revert("NAME");

        Assert.IsFalse(draft.TryGetChange("name", out _));
        CollectionAssert.AreEqual(new[] { "artist" }, draft.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { P("name", "Old"), P("artist", "B") }, draft.ApplyTo([P("name", "Old"), P("artist", "A")]));
    }

    [TestMethod]
    public void ClearEmptiesTheDraft()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");

        draft.Clear();

        Assert.IsTrue(draft.IsEmpty);
        Assert.IsEmpty(draft.Keys);
    }

    [TestMethod]
    public void ArtChangeMakesTheDraftDirtyWithoutMetadataChanges()
    {
        var draft = new MetadataDraft();
        draft.SetAlbumArt(@"C:\art\cover.png");

        Assert.IsFalse(draft.IsEmpty);
        Assert.IsFalse(draft.HasMetadataChanges);
        Assert.AreEqual(new AlbumArtChange(@"C:\art\cover.png"), draft.AlbumArt);
        Assert.IsEmpty(draft.Keys);
        CollectionAssert.AreEqual(new[] { P("name", "A") }, draft.ApplyTo([P("name", "A")]));
    }

    [TestMethod]
    public void RemoveArtReplacesAnEarlierReplacement()
    {
        var draft = new MetadataDraft();
        draft.SetAlbumArt("cover.png");

        draft.RemoveAlbumArt();

        Assert.IsTrue(draft.AlbumArt is { IsRemoval: true });
    }

    [TestMethod]
    public void RevertArtLeavesMetadataChanges()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");
        draft.RemoveAlbumArt();

        draft.RevertAlbumArt();

        Assert.IsNull(draft.AlbumArt);
        Assert.IsTrue(draft.HasMetadataChanges);
        Assert.IsFalse(draft.IsEmpty);
    }

    [TestMethod]
    public void ClearForgetsTheArtChange()
    {
        var draft = new MetadataDraft();
        draft.RemoveAlbumArt();

        draft.Clear();

        Assert.IsNull(draft.AlbumArt);
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void FileChangesMakeTheDraftDirtyWithoutMetadataChanges()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("notes.chart");
        draft.AddFile(new AddedFile("guitar.ogg", @"C:\src\guitar.ogg", 10));

        Assert.IsFalse(draft.IsEmpty);
        Assert.IsTrue(draft.HasFileChanges);
        Assert.IsFalse(draft.HasMetadataChanges);
        Assert.IsNull(draft.AlbumArt);
        Assert.IsTrue(draft.IsFileRemoved("NOTES.chart"));
        Assert.IsTrue(draft.IsFileAdded("Guitar.OGG"));
    }

    [TestMethod]
    public void AddingANameAgainReplacesTheEarlierAdd()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", "a.ogg", 1));
        draft.AddFile(new AddedFile("drums.ogg", "d.ogg", 3));

        draft.AddFile(new AddedFile("GUITAR.ogg", "b.ogg", 2));

        CollectionAssert.AreEqual(
            new[] { new AddedFile("GUITAR.ogg", "b.ogg", 2), new AddedFile("drums.ogg", "d.ogg", 3) },
            draft.AddedFiles.ToArray());
    }

    [TestMethod]
    public void RevertFileForgetsBothTheAddAndTheRemoval()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("song.ogg");
        draft.AddFile(new AddedFile("song.ogg", "new.ogg", 1));
        draft.RemoveFile("notes.chart");

        draft.RevertFile("SONG.ogg");

        Assert.IsFalse(draft.IsFileRemoved("song.ogg"));
        Assert.IsFalse(draft.IsFileAdded("song.ogg"));
        CollectionAssert.AreEqual(new[] { "notes.chart" }, draft.RemovedFiles.ToArray());
        Assert.IsEmpty(draft.AddedFiles);
    }

    [TestMethod]
    public void RevertingEveryFileChangeLeavesTheDraftEmpty()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("notes.chart");
        draft.AddFile(new AddedFile("guitar.ogg", "g.ogg", 1));

        draft.RevertFile("notes.chart");
        draft.RevertFile("guitar.ogg");

        Assert.IsTrue(draft.IsEmpty);
        Assert.IsFalse(draft.HasFileChanges);
    }

    [TestMethod]
    public void ClearForgetsTheFileChanges()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("notes.chart");
        draft.AddFile(new AddedFile("guitar.ogg", "g.ogg", 1));

        draft.Clear();

        Assert.IsTrue(draft.IsEmpty);
        Assert.IsEmpty(draft.RemovedFiles);
        Assert.IsEmpty(draft.AddedFiles);
    }

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);
}
