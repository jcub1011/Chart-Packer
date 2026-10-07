using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using static ChartBuilder.Tests.Charting.ChartSettingsCommandTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>The <c>song.ini</c> changes an open chart calls for (charter-spec C1 OK, C17 item 2).</summary>
[TestClass]
public sealed class PendingSongIniChangesTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Bass = ChartInstrument.Bass;
    private const ChartInstrument Drums = ChartInstrument.Drums;

    private static readonly IReadOnlySet<ChartInstrument> JustGuitar = new HashSet<ChartInstrument> { Guitar };

    [TestMethod]
    public void TheChartAsOpenedCallsForNothing()
    {
        var chart = NewChart(Guitar);

        Assert.IsEmpty(PendingSongIniChanges.Of(chart, JustGuitar, []));
    }

    [TestMethod]
    public void AnAddedPartCallsForAnUnratedDifficulty()
    {
        // AC C1.7.
        var chart = NewChart(Guitar);
        new AddPart(Bass).Apply(chart);

        var changes = PendingSongIniChanges.Of(chart, JustGuitar, []);

        Assert.AreSequenceEqual([new MetadataChange("diff_bass", "-1")], changes.ToList());
    }

    [TestMethod]
    public void AnAddedPartRatedAlreadyCallsForNothing()
    {
        var chart = NewChart(Guitar);
        new AddPart(Bass).Apply(chart);

        Assert.IsEmpty(PendingSongIniChanges.Of(chart, JustGuitar, [KeyValuePair.Create("diff_bass", "3")]));
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane, new[] { "diff_drums", "-1", "pro_drums", "False" })]
    [DataRow(DrumsVariant.Pro, new[] { "diff_drums", "-1", "diff_drums_real", "-1", "pro_drums", "True" })]
    [DataRow(DrumsVariant.FiveLane, new[] { "diff_drums", "-1", "five_lane_drums", "True" })]
    public void AddedDrumsCallForTheirRatingsAndLayout(DrumsVariant variant, string[] expected)
    {
        // AC C1.5, on an open song.
        var chart = NewChart(Guitar);
        new AddPart(Drums, variant).Apply(chart);

        var changes = PendingSongIniChanges.Of(chart, JustGuitar, []);

        Assert.AreSequenceEqual(expected.Chunk(2).Select(p => new MetadataChange(p[0], p[1])).ToList(), changes.ToList());
    }

    [TestMethod]
    public void AVariantSongIniHasAlreadyCallsForNothing()
    {
        var chart = NewChart(Drums);
        var drums = new HashSet<ChartInstrument> { Drums };

        Assert.IsEmpty(PendingSongIniChanges.Of(chart, drums, [KeyValuePair.Create("pro_drums", "true")]));
    }

    [TestMethod]
    public void AVariantDetectedOnLoadCallsForItsKey()
    {
        // charter-spec §4.1: a detected variant is written on the next save.
        var chart = NewChart(Drums);

        var changes = PendingSongIniChanges.Of(chart, new HashSet<ChartInstrument> { Drums }, []);

        Assert.AreSequenceEqual([new MetadataChange("pro_drums", "True")], changes.ToList());
    }

    [TestMethod]
    public void SwitchingFromFiveLaneTurnsItOff()
    {
        var chart = NewChart(Drums);
        var pairs = new[] { KeyValuePair.Create("five_lane_drums", "True") };

        var changes = PendingSongIniChanges.Of(chart, new HashSet<ChartInstrument> { Drums }, pairs);

        Assert.AreSequenceEqual([new MetadataChange("pro_drums", "True"), new MetadataChange("five_lane_drums", "False")], changes.ToList());
    }

    [TestMethod]
    [DataRow(DrumsVariant.FourLane)]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FiveLane)]
    public void ANewChartCallsForThePartKeysItsFirstSaveWrites(DrumsVariant variant)
    {
        // A new chart has no song.ini yet, so every part's rating and the drum keys are pending, as NewChartIni writes them.
        var settings = new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar, Drums }, variant);
        var song = EditorSong.New(EditableChart.CreateNew(settings), []);
        var request = new NewChartRequest { ParentFolder = "C:\\", FolderName = "Song", Chart = settings };

        var changes = PendingSongIniChanges.Of(song.Chart, song.LoadedParts, song.Pairs);

        var written = NewChartIni.Pairs(request, null).Where(p => p.Key.StartsWith("diff_", StringComparison.Ordinal) || p.Key.Contains("drums", StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(written.Select(p => new MetadataChange(p.Key, p.Value)).ToList(), changes.ToList());
    }

    [TestMethod]
    public void UndoingTheAddTakesItsChangeAway()
    {
        // AC C1.7: shown as soon as OK adds the part, and gone once it's undone.
        var chart = NewChart(Guitar);
        var stack = new ChartUndoStack(chart);
        var values = ChartSettingsValues.Of(chart) with { Parts = new HashSet<ChartInstrument> { Guitar, Bass } };

        stack.Execute(ChartSettingsEdit.CommandFor(chart, values, keepTimes: false)!);
        Assert.HasCount(1, PendingSongIniChanges.Of(chart, JustGuitar, []));

        stack.Undo();
        Assert.IsEmpty(PendingSongIniChanges.Of(chart, JustGuitar, []));
    }
}
