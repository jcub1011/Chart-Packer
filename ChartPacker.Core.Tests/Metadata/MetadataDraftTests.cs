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

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);
}
