using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class BulkMetadataTests
{
    [TestMethod]
    public void RowsAreTheUnionSortedByKey()
    {
        var items = Items([P("name", "A"), P("artist", "X")], [P("album", "Z"), P("NAME", "A")]);

        var rows = BulkMetadata.BuildRows(items);

        Assert.AreSequenceEqual(["album", "artist", "name"], [.. rows.Select(r => r.Key)]);
    }

    [TestMethod]
    public void DraftedKeySortsAmongSavedKeys()
    {
        var items = Items([P("name", "A"), P("Charter", "C")]);

        BulkMetadata.Set(items, "album", "Z");

        Assert.AreSequenceEqual(["album", "Charter", "name"], [.. BulkMetadata.BuildRows(items).Select(r => r.Key)]);
    }

    [TestMethod]
    public void SharedValueShowsAndDifferingValueIsDistinct()
    {
        var items = Items([P("name", "Same"), P("artist", "X")], [P("name", "Same"), P("artist", "Y")]);

        var rows = BulkMetadata.BuildRows(items);

        Assert.AreEqual(new BulkMetadataRow("name", "Same", MetadataRowState.Unchanged), rows.Single(r => r.Key == "name"));
        Assert.IsTrue(rows.Single(r => r.Key == "artist").IsDistinct);
    }

    [TestMethod]
    public void KeyMissingFromOneItemIsDistinct()
    {
        var items = Items([P("name", "Same"), P("album", "Z")], [P("name", "Same")]);

        var album = BulkMetadata.BuildRows(items).Single(r => r.Key == "album");

        Assert.IsTrue(album.IsDistinct);
        Assert.AreEqual(MetadataRowState.Unchanged, album.State);
    }

    [TestMethod]
    public void EditingOneKeyAcrossItemsLeavesTheirOtherValuesAlone()
    {
        var originals = new[]
        {
            new[] { P("name", "Song 1"), P("artist", "Band A"), P("year", "1999") },
            [P("name", "Song 2"), P("artist", "Band B")],
            [P("artist", "Band C"), P("name", "Song 3")],
        };
        var items = Items(originals);

        BulkMetadata.Set(items, "name", "Renamed");

        var rows = BulkMetadata.BuildRows(items);
        Assert.AreEqual(new BulkMetadataRow("name", "Renamed", MetadataRowState.Edited), rows.Single(r => r.Key == "name"));
        Assert.IsTrue(rows.Single(r => r.Key == "artist").IsDistinct);

        Assert.AreSequenceEqual([P("name", "Renamed"), P("artist", "Band A"), P("year", "1999")], items[0].Draft.ApplyTo(originals[0]));
        Assert.AreSequenceEqual([P("name", "Renamed"), P("artist", "Band B")], items[1].Draft.ApplyTo(originals[1]));
        Assert.AreSequenceEqual([P("artist", "Band C"), P("name", "Renamed")], items[2].Draft.ApplyTo(originals[2]));
        Assert.IsTrue(items.All(i => i.Draft.Keys.SequenceEqual(["name"])));
    }

    [TestMethod]
    public void SettingADistinctRowAddsTheKeyWhereItIsMissing()
    {
        var items = Items([P("album", "Z")], [P("name", "B")]);

        BulkMetadata.Set(items, "album", "New");

        var album = BulkMetadata.BuildRows(items).Single(r => r.Key == "album");
        Assert.AreEqual(new BulkMetadataRow("album", "New", MetadataRowState.Edited), album);
        Assert.IsTrue(items[1].Draft.TryGetChange("album", out _));
    }

    [TestMethod]
    public void SettingTheSavedValueLeavesNoChange()
    {
        var items = Items([P("name", "Same")], [P("name", "Other")]);

        BulkMetadata.Set(items, "name", "Same");

        Assert.IsTrue(items[0].Draft.IsEmpty);
        Assert.IsFalse(items[1].Draft.IsEmpty);

        BulkMetadata.Set(items[1..], "name", "Other");
        Assert.AreEqual(MetadataRowState.Unchanged, BulkMetadata.BuildRows(items).Single().State);
    }

    [TestMethod]
    public void RemoveOnlyTouchesItemsThatHaveTheKey()
    {
        var items = Items([P("name", "A"), P("album", "Z")], [P("name", "B")]);

        BulkMetadata.Remove(items, "album");

        Assert.IsTrue(items[0].Draft.TryGetChange("album", out var change) && change.IsRemoval);
        Assert.IsTrue(items[1].Draft.IsEmpty);
        var album = BulkMetadata.BuildRows(items).Single(r => r.Key == "album");
        Assert.AreEqual(MetadataRowState.Removed, album.State);
    }

    [TestMethod]
    public void RemovedRowShowsTheSavedValue()
    {
        var items = Items([P("album", "Z")], [P("album", "Z")]);

        BulkMetadata.Remove(items, "album");

        Assert.AreEqual(new BulkMetadataRow("album", "Z", MetadataRowState.Removed), BulkMetadata.BuildRows(items).Single());
    }

    [TestMethod]
    public void NewKeyIsAddedAndRemovingItAgainDropsTheRow()
    {
        var items = Items([P("name", "A")], [P("name", "B")]);

        BulkMetadata.Set(items, "genre", "Rock");
        Assert.AreEqual(new BulkMetadataRow("genre", "Rock", MetadataRowState.Added), BulkMetadata.BuildRows(items).Single(r => r.Key == "genre"));

        BulkMetadata.Remove(items, "genre");
        Assert.DoesNotContain(r => r.Key == "genre", BulkMetadata.BuildRows(items));
        Assert.IsTrue(items.All(i => i.Draft.IsEmpty));
    }

    [TestMethod]
    public void RevertRestoresEveryItem()
    {
        var items = Items([P("name", "A")], [P("name", "B")]);
        BulkMetadata.Set(items, "name", "C");

        BulkMetadata.Revert(items, "name");

        Assert.IsTrue(items.All(i => i.Draft.IsEmpty));
        var name = BulkMetadata.BuildRows(items).Single();
        Assert.AreEqual(MetadataRowState.Unchanged, name.State);
        Assert.IsTrue(name.IsDistinct);
    }

    [TestMethod]
    public void UnsetDifficultyIsHiddenUntilEdited()
    {
        var items = Items([P("name", "A"), P("diff_bass", "-1")]);
        Assert.DoesNotContain(r => r.Key == "diff_bass", BulkMetadata.BuildRows(items));

        BulkMetadata.Set(items, "diff_bass", "3");

        Assert.AreEqual(new BulkMetadataRow("diff_bass", "3", MetadataRowState.Edited), BulkMetadata.BuildRows(items).Single(r => r.Key == "diff_bass"));
    }

    [TestMethod]
    public void DifficultySetInOneItemShowsEvenIfUnsetInAnother()
    {
        var items = Items([P("diff_bass", "-1")], [P("diff_bass", "4")]);

        var row = BulkMetadata.BuildRows(items).Single();

        Assert.AreEqual("diff_bass", row.Key);
        Assert.IsTrue(row.IsDistinct);
    }

    [TestMethod]
    public void OneItemWorksTheSameWay()
    {
        var items = Items([P("name", "A"), P("artist", "X")]);

        BulkMetadata.Set(items, "name", "B");
        BulkMetadata.Remove(items, "artist");

        var rows = BulkMetadata.BuildRows(items);
        Assert.AreEqual(new BulkMetadataRow("artist", "X", MetadataRowState.Removed), rows[0]);
        Assert.AreEqual(new BulkMetadataRow("name", "B", MetadataRowState.Edited), rows[1]);
    }

    [TestMethod]
    public void ExcludedKeysHaveNoRow()
    {
        var items = Items([P("name", "A"), P("diff_guitar", "4"), P("diff_foo", "2")]);

        var rows = BulkMetadata.BuildRows(items, DifficultySlots.Contains);

        Assert.AreSequenceEqual(["diff_foo", "name"], [.. rows.Select(r => r.Key)]);
    }

    [TestMethod]
    public void RowOfKeepsAnUnsetDifficulty()
    {
        var items = Items([P("diff_guitar", "-1")]);

        Assert.AreEqual(new BulkMetadataRow("diff_guitar", "-1", MetadataRowState.Unchanged), BulkMetadata.RowOf(items, "diff_guitar"));
        Assert.DoesNotContain(r => r.Key == "diff_guitar", BulkMetadata.BuildRows(items));
    }

    [TestMethod]
    public void SettingTheSavedValueOfARepeatedSongIniKeyKeepsTheChange()
    {
        var item = new MetadataItem([P("name", "A")], new MetadataDraft(), ["Name"]);

        BulkMetadata.Set([item], "name", "A");

        Assert.IsTrue(item.Draft.TryGetChange("name", out var change));
        Assert.AreEqual("A", change.Value);
    }

    [TestMethod]
    public void RemainingDuplicateKeysLeaveOutTheKeysTheDraftChanges()
    {
        var item = new MetadataItem([P("name", "A"), P("artist", "B"), P("genre", "C")], new MetadataDraft(), ["name", "artist", "genre"]);
        item.Draft.Set("NAME", "A");
        item.Draft.Remove("artist");

        Assert.AreSequenceEqual(["genre"], [.. item.RemainingDuplicateKeys()]);
    }

    [TestMethod]
    public void RowOfAKeyNoItemHasIsNull() =>
        Assert.IsNull(BulkMetadata.RowOf(Items([P("name", "A")]), "diff_guitar"));

    private static MetadataItem[] Items(params KeyValuePair<string, string>[][] originals) =>
        [.. originals.Select(o => new MetadataItem(o, new MetadataDraft()))];

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);
}
