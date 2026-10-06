using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Tests.Charting;

[TestClass]
public sealed class NewChartIniTests
{
    private static NewChartRequest Request(DrumsVariant drums = DrumsVariant.Pro, params ChartInstrument[] parts) => new()
    {
        ParentFolder = "unused",
        FolderName = "unused",
        Name = " Song ",
        Artist = "Artist",
        Album = "",
        Genre = "Rock",
        Year = "1999",
        Charter = "Me",
        Chart = new NewChartSettings("Song", 480, 120, 4, 4, parts.ToHashSet(), drums),
    };

    [TestMethod]
    public void MetadataIsTrimmedAndEmptyValuesAreLeftOut()
    {
        var pairs = NewChartIni.Pairs(Request(DrumsVariant.Pro, ChartInstrument.Guitar), 183_000);

        Assert.AreSequenceEqual(
        [
            KeyValuePair.Create("name", "Song"),
            KeyValuePair.Create("artist", "Artist"),
            KeyValuePair.Create("genre", "Rock"),
            KeyValuePair.Create("year", "1999"),
            KeyValuePair.Create("charter", "Me"),
            KeyValuePair.Create("song_length", "183000"),
            KeyValuePair.Create("diff_guitar", "-1"),
        ], [.. pairs]);
    }

    [TestMethod]
    public void EveryPartIsUnratedUnderItsOwnKey()
    {
        var pairs = NewChartIni.Pairs(Request(DrumsVariant.FourLane, [.. NewChartSettings.Instruments]), null);

        var keys = pairs.Where(p => p.Key.StartsWith("diff_", StringComparison.Ordinal)).ToList();
        Assert.AreSequenceEqual(
            ["diff_guitar", "diff_bass", "diff_rhythm", "diff_guitar_coop", "diff_guitarghl", "diff_bassghl", "diff_rhythm_ghl", "diff_guitar_coop_ghl", "diff_drums"],
            [.. keys.Select(p => p.Key)]);
        Assert.IsTrue(keys.All(p => p.Value == "-1"));
        Assert.IsFalse(pairs.Any(p => p.Key == "song_length"));
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FiveLane)]
    public void TheDrumVariantReadsBackAsItsLayout(DrumsVariant variant)
    {
        // AC C1.5: the games, and the app's reader, lay the drums out as chosen.
        var pairs = NewChartIni.Pairs(Request(variant, ChartInstrument.Drums), null);

        var layout = new DrumsLayouts(ChartReadOptions.From(pairs), midi: true).Layout;

        Assert.AreEqual(variant switch
        {
            DrumsVariant.FourLane => DrumsLayout.FourLane,
            DrumsVariant.Pro => DrumsLayout.Pro,
            _ => DrumsLayout.FiveLane,
        }, layout);
    }

    [TestMethod]
    public void ProDrumsAreSaidAndRatedAsPro()
    {
        // AC C1.5.
        var pro = NewChartIni.Pairs(Request(DrumsVariant.Pro, ChartInstrument.Drums), null);
        var fiveLane = NewChartIni.Pairs(Request(DrumsVariant.FiveLane, ChartInstrument.Drums), null);
        var fourLane = NewChartIni.Pairs(Request(DrumsVariant.FourLane, ChartInstrument.Drums), null);

        CollectionAssert.Contains(pro.ToList(), KeyValuePair.Create("pro_drums", "True"));
        CollectionAssert.Contains(pro.ToList(), KeyValuePair.Create("diff_drums_real", "-1"));
        CollectionAssert.Contains(fiveLane.ToList(), KeyValuePair.Create("five_lane_drums", "True"));
        Assert.IsFalse(fiveLane.Any(p => p.Key == "diff_drums_real"));
        CollectionAssert.Contains(fourLane.ToList(), KeyValuePair.Create("pro_drums", "False"));
    }

    [TestMethod]
    public void WithoutDrumsNoDrumKeysAreWritten()
    {
        var pairs = NewChartIni.Pairs(Request(DrumsVariant.FiveLane, ChartInstrument.Bass), null);

        Assert.IsFalse(pairs.Any(p => p.Key is "pro_drums" or "five_lane_drums" or "diff_drums"));
    }

    [TestMethod]
    public void TheLeadInIsTheDelay()
    {
        var pairs = NewChartIni.Pairs(Request(DrumsVariant.Pro, ChartInstrument.Guitar) with { LeadInMs = 1500 }, null);

        CollectionAssert.Contains(pairs.ToList(), KeyValuePair.Create("delay", "1500"));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500), SongChartSettings.DelayOf(pairs, (TimeSpan?)null));
    }

    [TestMethod]
    public void TheMetadataValidates()
    {
        var pairs = NewChartIni.Pairs(Request(DrumsVariant.Pro, [.. NewChartSettings.Instruments]) with { LeadInMs = 20 }, 1000);

        Assert.IsEmpty(MetadataValidator.Validate(pairs).Where(MetadataValidator.IsFailing));
    }
}
