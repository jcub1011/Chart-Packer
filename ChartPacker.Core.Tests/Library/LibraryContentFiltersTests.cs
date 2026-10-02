using ChartPacker.Core.Charts;
using ChartPacker.Core.Library;

namespace ChartPacker.Core.Tests.Library;

[TestClass]
public sealed class LibraryContentFiltersTests
{
    private const LibraryInstruments GuitarDrums = LibraryInstruments.Guitar | LibraryInstruments.Drums;

    [TestMethod]
    public void OffFilterPassesEverything()
    {
        var filter = default(SetFilter<LibraryInstruments>);

        Assert.IsFalse(filter.IsActive);
        Assert.IsTrue(filter.Matches(null));
        Assert.IsTrue(filter.Matches(LibraryInstruments.None));
    }

    [TestMethod]
    public void AllNeedsEveryIncludedItem()
    {
        var filter = new SetFilter<LibraryInstruments>(GuitarDrums, LibraryInstruments.None, SetMatch.All);

        Assert.IsTrue(filter.Matches(GuitarDrums | LibraryInstruments.Bass));
        Assert.IsFalse(filter.Matches(LibraryInstruments.Guitar));
    }

    [TestMethod]
    public void AnyNeedsOneIncludedItem()
    {
        var filter = new SetFilter<LibraryInstruments>(GuitarDrums, LibraryInstruments.None, SetMatch.Any);

        Assert.IsTrue(filter.Matches(LibraryInstruments.Drums));
        Assert.IsFalse(filter.Matches(LibraryInstruments.Bass));
    }

    [TestMethod]
    [DataRow(SetMatch.All)]
    [DataRow(SetMatch.Any)]
    public void AnExcludedItemAlwaysFails(SetMatch match)
    {
        var filter = new SetFilter<LibraryInstruments>(LibraryInstruments.Guitar, LibraryInstruments.Vocals, match);

        Assert.IsTrue(filter.Matches(GuitarDrums));
        Assert.IsFalse(filter.Matches(LibraryInstruments.Guitar | LibraryInstruments.Vocals));
    }

    [TestMethod]
    [DataRow(SetMatch.All)]
    [DataRow(SetMatch.Any)]
    public void OnlyExcludedItemsPassSongsWithoutThem(SetMatch match)
    {
        var filter = new SetFilter<LibraryDifficulties>(LibraryDifficulties.None, LibraryDifficulties.Expert, match);

        Assert.IsTrue(filter.Matches(LibraryDifficulties.Easy));
        Assert.IsTrue(filter.Matches(LibraryDifficulties.None));
        Assert.IsFalse(filter.Matches(LibraryDifficulties.Hard | LibraryDifficulties.Expert));
    }

    [TestMethod]
    public void OnlyTheseIsIncludedPlusTheRestExcluded()
    {
        const LibraryStems others = LibraryStems.Rhythm | LibraryStems.Bass | LibraryStems.Keys | LibraryStems.Drums | LibraryStems.Vocals | LibraryStems.Crowd | LibraryStems.Preview;
        var filter = new SetFilter<LibraryStems>(LibraryStems.Song | LibraryStems.Guitar, others);

        Assert.IsTrue(filter.Matches(LibraryStems.Song | LibraryStems.Guitar));
        Assert.IsFalse(filter.Matches(LibraryStems.Song | LibraryStems.Guitar | LibraryStems.Drums));
        Assert.IsFalse(filter.Matches(LibraryStems.Song));
    }

    [TestMethod]
    public void UnknownValueFailsAnActiveFilter() =>
        Assert.IsFalse(new SetFilter<LibraryStems>(LibraryStems.None, LibraryStems.Crowd).Matches(null));

    [TestMethod]
    public void FiltersWithTheSameItemsAreEqual() =>
        Assert.AreEqual(
            new SetFilter<LibraryInstruments>(GuitarDrums, LibraryInstruments.Vocals, SetMatch.Any),
            new SetFilter<LibraryInstruments>(LibraryInstruments.Drums | LibraryInstruments.Guitar, LibraryInstruments.Vocals, SetMatch.Any));

    [TestMethod]
    [DataRow(FilterPresence.Either, true, true)]
    [DataRow(FilterPresence.Either, false, true)]
    [DataRow(FilterPresence.Either, null, true)]
    [DataRow(FilterPresence.Includes, true, true)]
    [DataRow(FilterPresence.Includes, false, false)]
    [DataRow(FilterPresence.Includes, null, false)]
    [DataRow(FilterPresence.Excludes, true, false)]
    [DataRow(FilterPresence.Excludes, false, true)]
    [DataRow(FilterPresence.Excludes, null, false)]
    public void PresenceMatches(FilterPresence presence, bool? has, bool expected) =>
        Assert.AreEqual(expected, LibraryContentFilters.PresenceMatches(presence, has));

    [TestMethod]
    [DataRow("song", LibraryStems.Song)]
    [DataRow("drums", LibraryStems.Drums)]
    [DataRow("drums_3", LibraryStems.Drums)]
    [DataRow("vocals_2", LibraryStems.Vocals)]
    [DataRow("preview", LibraryStems.Preview)]
    [DataRow("DRUMS_1", LibraryStems.Drums)]
    [DataRow("drums_9", LibraryStems.None)]
    [DataRow("notes", LibraryStems.None)]
    public void StemGroups(string stem, LibraryStems expected) =>
        Assert.AreEqual(expected, LibraryContentFilters.StemGroupOf(stem));

    [TestMethod]
    public void DifficultiesAndTheirFlagsGoBothWays()
    {
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            Assert.AreSequenceEqual([difficulty], difficulty.ToFlag().ToDifficulties(), difficulty.ToString());

        Assert.AreSequenceEqual(
            [ChartDifficulty.Easy, ChartDifficulty.Hard], (LibraryDifficulties.Hard | LibraryDifficulties.Easy).ToDifficulties());
        Assert.IsEmpty(LibraryDifficulties.None.ToDifficulties());
    }
}
