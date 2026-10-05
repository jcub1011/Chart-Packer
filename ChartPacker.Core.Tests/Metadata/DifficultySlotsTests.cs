using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class DifficultySlotsTests
{
    [TestMethod]
    public void EveryKnownDifficultyHasOneSlot()
    {
        var known = KnownKeys.All.Select(k => k.Key).Where(MetadataDifficulty.IsDifficultyKey).Order(StringComparer.Ordinal).ToList();

        Assert.AreSequenceEqual(known, [.. DifficultySlots.All.Select(s => s.Key).Order(StringComparer.Ordinal)]);
    }

    [TestMethod]
    public void SlotsRateThePartThatOwnsTheirKey()
    {
        foreach (var slot in DifficultySlots.All)
            Assert.AreEqual(MidiPartCatalog.OwnerOf(slot.Key)?.Id, slot.PartId, slot.Key);
    }

    [TestMethod]
    public void OnlyTheBandRatesNoPart() =>
        Assert.AreSequenceEqual(["diff_band"], [.. DifficultySlots.All.Where(s => s.PartId is null).Select(s => s.Key)]);

    [TestMethod]
    public void ContainsIgnoresCase()
    {
        Assert.IsTrue(DifficultySlots.Contains("DIFF_Guitar"));
        Assert.IsFalse(DifficultySlots.Contains("diff_foo"));
        Assert.IsFalse(DifficultySlots.Contains("delay"));
    }
}
