using ChartShared.Core.Charts;
using ChartShared.Core.Merging;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Tests.Charts;

namespace ChartShared.Core.Tests.Metadata;

[TestClass]
public sealed class MetadataDraftTests
{
    [TestMethod]
    public void SetReplacesValueInPlaceKeepingStoredKeyCase()
    {
        var draft = new MetadataDraft();
        draft.Set("NAME", "New");

        var result = draft.ApplyTo([P("artist", "A"), P("Name", "Old"), P("album", "B")]);

        Assert.AreSequenceEqual([P("artist", "A"), P("Name", "New"), P("album", "B")], result);
    }

    [TestMethod]
    public void SetCollapsesDuplicatesOfThatKey()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");

        var result = draft.ApplyTo([P("name", "One"), P("artist", "A"), P("name", "Two")]);

        Assert.AreSequenceEqual([P("name", "New"), P("artist", "A")], result);
    }

    [TestMethod]
    public void RemoveDropsEveryDuplicate()
    {
        var draft = new MetadataDraft();
        draft.Remove("Name");

        var result = draft.ApplyTo([P("name", "One"), P("artist", "A"), P("NAME", "Two")]);

        Assert.AreSequenceEqual([P("artist", "A")], result);
    }

    [TestMethod]
    public void MissingKeysAreAppendedInDraftOrder()
    {
        var draft = new MetadataDraft();
        draft.Set("year", "2001");
        draft.Set("genre", "Rock");

        var result = draft.ApplyTo([P("name", "Title")]);

        Assert.AreSequenceEqual([P("name", "Title"), P("year", "2001"), P("genre", "Rock")], result);
    }

    [TestMethod]
    public void UneditedPairsAndTheirDuplicatesAreUntouched()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "New");
        var pairs = new[] { P("artist", "A"), P("artist", "B"), P("name", "Old"), P("icon", "x") };

        var result = draft.ApplyTo(pairs);

        Assert.AreSequenceEqual([P("artist", "A"), P("artist", "B"), P("name", "New"), P("icon", "x")], result);
    }

    [TestMethod]
    public void RemovingAMissingKeyChangesNothing()
    {
        var draft = new MetadataDraft();
        draft.Remove("year");
        var pairs = new[] { P("name", "Title") };

        Assert.AreSequenceEqual(pairs, draft.ApplyTo(pairs));
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
        Assert.AreSequenceEqual(["artist"], [.. draft.Keys]);
        Assert.AreSequenceEqual([P("name", "Old"), P("artist", "B")], draft.ApplyTo([P("name", "Old"), P("artist", "A")]));
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
        Assert.AreSequenceEqual([P("name", "A")], draft.ApplyTo([P("name", "A")]));
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
        Assert.AreSequenceEqual([KeyValuePair.Create(hard, -5)], [.. draft.ChartOffsets]);

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

        Assert.AreSequenceEqual(["name", "artist"], [.. clone.Keys]);
        Assert.AreEqual(new AlbumArtChange("cover.png"), clone.AlbumArt);
        Assert.AreEqual(new BackgroundChange("clip.webm"), clone.BackgroundVideo);
        Assert.AreSequenceEqual(["video.mp4"], [.. clone.RemovedFiles]);
        Assert.IsEmpty(clone.AddedFiles);
        Assert.AreSequenceEqual([KeyValuePair.Create(track, 40)], [.. clone.ChartOffsets]);
        Assert.AreEqual(new VocalsChange(source), clone.Vocals);

        // Reverting the clone's add brought back only its own copy of the file it replaced.
        Assert.AreSequenceEqual(["video.mp4", "guitar.ogg"], [.. draft.RemovedFiles], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["name", "artist", "album"], [.. draft.Keys]);
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

        Assert.AreSequenceEqual(
            [new AddedFile("GUITAR.ogg", "b.ogg", 2), new AddedFile("drums.ogg", "d.ogg", 3)], [.. draft.AddedFiles]);
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
        Assert.AreSequenceEqual(["notes.chart"], [.. draft.RemovedFiles]);
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
    public void RevertingAnAddPutsBackTheKeysItChanged()
    {
        var draft = new MetadataDraft();
        draft.Set("delay", "100");
        draft.AddFile(new AddedFile("notes.mid", "n.mid", 1), ["notes.chart"], [new MetadataChange("delay", "500"), new MetadataChange("pro_drums", null)]);
        Assert.AreEqual("500", Change(draft, "delay"));
        Assert.IsTrue(draft.TryGetChange("pro_drums", out _));

        draft.RevertFile("notes.mid");

        Assert.AreEqual("100", Change(draft, "delay"));
        Assert.IsFalse(draft.TryGetChange("pro_drums", out _));
        Assert.IsFalse(draft.IsFileRemoved("notes.chart"));
    }

    [TestMethod]
    public void RevertingAnAddLeavesAKeyChangedSince()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.mid", "n.mid", 1), [], [new MetadataChange("delay", "500")]);
        draft.Set("DELAY", "250");

        draft.RevertFile("notes.mid");

        Assert.AreEqual("250", Change(draft, "delay"));
    }

    [TestMethod]
    public void AddingAgainKeepsWhatTheKeysWereBeforeTheFirstAdd()
    {
        var draft = new MetadataDraft();
        draft.Set("delay", "100");
        draft.AddFile(new AddedFile("notes.mid", "a.mid", 1), [], [new MetadataChange("delay", "500")]);
        draft.AddFile(new AddedFile("notes.mid", "b.mid", 1), [], [new MetadataChange("delay", "600"), new MetadataChange("five_lane_drums", "True")]);

        draft.RevertFile("notes.mid");

        Assert.AreEqual("100", Change(draft, "delay"));
        Assert.IsFalse(draft.TryGetChange("five_lane_drums", out _));
    }

    [TestMethod]
    public void ACloneRevertsTheKeysOfAnAddOnItsOwn()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.mid", "n.mid", 1), [], [new MetadataChange("delay", "500")]);
        var copy = draft.Clone();

        copy.RevertFile("notes.mid");

        Assert.IsTrue(copy.IsEmpty);
        Assert.AreEqual("500", Change(draft, "delay"));
    }

    private static string? Change(MetadataDraft draft, string key) =>
        draft.TryGetChange(key, out var change) ? change.Value : throw new AssertFailedException($"{key} has no change.");

    [TestMethod]
    public void RevertingAReplacingAddKeepsARemovalMadeBeforeIt()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("guitar.mp3");
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["GUITAR.mp3"]);

        draft.RevertFile("guitar.ogg");

        Assert.AreSequenceEqual(["guitar.mp3"], [.. draft.RemovedFiles]);
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

        Assert.AreSequenceEqual(["guitar.mp3"], [.. draft.RemovedFiles]);
    }

    [TestMethod]
    public void ReplacingAStemAgainLeavesOneRemoval()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("guitar.ogg", "x.ogg", 1), ["guitar.mp3"]);

        // As adding a track does: the stem's earlier add goes first.
        draft.RevertFile("guitar.ogg");
        draft.AddFile(new AddedFile("guitar.opus", "y.opus", 2), ["guitar.mp3"]);

        Assert.AreSequenceEqual(["guitar.mp3"], [.. draft.RemovedFiles]);
        Assert.AreSequenceEqual([new AddedFile("guitar.opus", "y.opus", 2)], [.. draft.AddedFiles]);

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
