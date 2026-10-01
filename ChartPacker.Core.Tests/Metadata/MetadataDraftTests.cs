using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Tests.Charts;

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
    public void BackgroundChangeMakesTheDraftDirtyWithoutOtherChanges()
    {
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Video, @"C:\src\clip.webm");

        Assert.IsFalse(draft.IsEmpty);
        Assert.IsTrue(draft.HasBackgroundChanges);
        Assert.IsFalse(draft.HasMetadataChanges);
        Assert.IsFalse(draft.HasFileChanges);
        Assert.IsNull(draft.AlbumArt);
        Assert.AreEqual(new BackgroundChange(@"C:\src\clip.webm"), draft.BackgroundVideo);
        Assert.AreEqual(draft.BackgroundVideo, draft.GetBackground(BackgroundKind.Video));
        Assert.IsNull(draft.BackgroundImage);
    }

    [TestMethod]
    public void BackgroundKindsAreChangedIndependently()
    {
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, "bg.png");
        draft.RemoveBackground(BackgroundKind.Video);
        draft.SetAlbumArt("cover.png");

        draft.RevertBackground(BackgroundKind.Image);

        Assert.IsNull(draft.BackgroundImage);
        Assert.IsTrue(draft.BackgroundVideo is { IsRemoval: true });
        Assert.AreEqual(new AlbumArtChange("cover.png"), draft.AlbumArt);
    }

    [TestMethod]
    public void RemoveBackgroundReplacesAnEarlierReplacement()
    {
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, "bg.png");

        draft.RemoveBackground(BackgroundKind.Image);

        Assert.IsTrue(draft.BackgroundImage is { IsRemoval: true });
    }

    [TestMethod]
    public void ChartOffsetsMakeTheDraftDirtyAndZeroForgetsOne()
    {
        var expert = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert);
        var hard = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Hard);
        var draft = new MetadataDraft();

        draft.SetChartOffset(expert, 12);
        draft.SetChartOffset(hard, -5);
        draft.SetChartOffset(expert, 0);

        Assert.IsFalse(draft.IsEmpty);
        Assert.IsTrue(draft.HasChartOffsets);
        Assert.IsFalse(draft.HasFileChanges);
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create(hard, -5) }, draft.ChartOffsets.ToArray());

        draft.Clear();

        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void VocalsChangeMakesTheDraftDirtyAndEditsTheChart()
    {
        var draft = new MetadataDraft();
        var source = Source("other.mid");

        draft.SetVocals(source);

        Assert.AreSame(source, draft.Vocals!.Value.Source);
        Assert.AreEqual("other.mid", draft.Vocals.Value.SourcePath);
        Assert.IsFalse(draft.IsEmpty);
        Assert.IsTrue(draft.HasChartEdits);
        Assert.IsFalse(draft.HasChartOffsets);
        Assert.IsFalse(draft.HasFileChanges);

        draft.RemoveVocals();

        Assert.AreEqual(new VocalsChange(null), draft.Vocals);
        Assert.IsTrue(draft.Vocals!.Value.IsRemoval);

        draft.RevertVocals();

        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void VocalsOffsetMakesTheDraftDirtyAndGoesWithTheChartOffsets()
    {
        var draft = new MetadataDraft();

        draft.SetVocalsOffset(-20);

        Assert.IsFalse(draft.IsEmpty);
        Assert.IsTrue(draft.HasVocalsOffset);
        Assert.IsTrue(draft.HasChartEdits);
        Assert.IsFalse(draft.HasChartOffsets);
        Assert.AreEqual(-20, draft.Clone().VocalsOffset);

        draft.ClearChartOffsets();

        Assert.AreEqual(0, draft.VocalsOffset);
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void ClearForgetsTheVocalsChange()
    {
        var draft = new MetadataDraft();
        draft.SetVocals(Source("other.mid"));

        draft.Clear();

        Assert.IsNull(draft.Vocals);
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void CloneCopiesEveryChangeAndNeitherSeesTheOthersLaterOnes()
    {
        var track = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Expert);
        var draft = new MetadataDraft();
        draft.Set("name", "A");
        draft.Remove("artist");
        draft.SetAlbumArt("cover.png");
        draft.SetBackground(BackgroundKind.Video, "clip.webm");
        draft.RemoveFile("video.mp4");
        draft.AddFile(new AddedFile("guitar.opus", "g.opus", 1), ["guitar.ogg"]);
        draft.SetChartOffset(track, 40);
        var source = Source("other.mid");
        draft.SetVocals(source);

        var clone = draft.Clone();
        draft.Set("album", "B");
        draft.RemoveVocals();
        draft.RevertBackground(BackgroundKind.Video);
        draft.AddFile(new AddedFile("notes.mid", "n.mid", 1));
        draft.SetChartOffset(track, 0);
        clone.RevertFile("guitar.opus");

        CollectionAssert.AreEqual(new[] { "name", "artist" }, clone.Keys.ToArray());
        Assert.AreEqual(new AlbumArtChange("cover.png"), clone.AlbumArt);
        Assert.AreEqual(new BackgroundChange("clip.webm"), clone.BackgroundVideo);
        CollectionAssert.AreEqual(new[] { "video.mp4" }, clone.RemovedFiles.ToArray());
        Assert.IsEmpty(clone.AddedFiles);
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create(track, 40) }, clone.ChartOffsets.ToArray());
        Assert.AreEqual(new VocalsChange(source), clone.Vocals);

        // Reverting the clone's add brought back only its own copy of the file it replaced.
        CollectionAssert.AreEquivalent(new[] { "video.mp4", "guitar.ogg" }, draft.RemovedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "name", "artist", "album" }, draft.Keys.ToArray());
        Assert.IsNull(draft.BackgroundVideo);
        Assert.IsFalse(draft.HasChartOffsets);
    }

    [TestMethod]
    public void ClearForgetsTheBackgroundChanges()
    {
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, "bg.png");
        draft.RemoveBackground(BackgroundKind.Video);

        draft.Clear();

        Assert.IsFalse(draft.HasBackgroundChanges);
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

    [TestMethod]
    public void RevertingAReplacingAddBringsBackWhatItReplaced()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["guitar.mp3"]);
        Assert.IsTrue(draft.IsFileRemoved("guitar.mp3"));

        draft.RevertFile("guitar.ogg");

        Assert.IsFalse(draft.IsFileRemoved("guitar.mp3"));
        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void RevertingAReplacingAddKeepsARemovalMadeBeforeIt()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("guitar.mp3");
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["GUITAR.mp3"]);

        draft.RevertFile("guitar.ogg");

        CollectionAssert.AreEqual(new[] { "guitar.mp3" }, draft.RemovedFiles.ToArray());
        Assert.IsEmpty(draft.AddedFiles);
    }

    [TestMethod]
    public void RevertingAReplacingAddKeepsARemovalAskedForAfterIt()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["guitar.mp3"]);
        draft.RevertFile("guitar.mp3");
        draft.RemoveFile("guitar.mp3");

        draft.RevertFile("guitar.ogg");

        CollectionAssert.AreEqual(new[] { "guitar.mp3" }, draft.RemovedFiles.ToArray());
    }

    [TestMethod]
    public void ReplacingAStemAgainLeavesOneRemoval()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["guitar.mp3"]);

        // As adding a track does: the stem's earlier add goes first.
        draft.RevertFile("guitar.ogg");
        draft.AddFile(new AddedFile("guitar.opus", "y.opus", 2), ["guitar.mp3"]);

        CollectionAssert.AreEqual(new[] { "guitar.mp3" }, draft.RemovedFiles.ToArray());
        CollectionAssert.AreEqual(new[] { new AddedFile("guitar.opus", "y.opus", 2) }, draft.AddedFiles.ToArray());

        draft.RevertFile("guitar.opus");

        Assert.IsTrue(draft.IsEmpty);
    }

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);

    private static VocalsSource Source(string path)
    {
        byte[] midi = new MidiBuilder().Track("PART VOCALS").Note(0, 60).Build();
        return new VocalsSource(path, new MidiChart(midi, TimeSpan.Zero, ChartReadOptions.Default), VocalReader.Read(midi, "notes.mid"));
    }
}
