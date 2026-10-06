using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class ChartModelComparerTests
{
    public static IEnumerable<object[]> ChangeNames => EditableChartSamples.Changes.Select(c => new object[] { c.Name });

    [TestMethod]
    public void IdenticalChartsHaveNoDifferences()
    {
        var chart = EditableChartSamples.Full();

        Assert.IsEmpty(ChartModelComparer.Differences(EditableChartSamples.Full(), chart));
        Assert.IsEmpty(ChartModelComparer.Differences(chart, chart.Clone()));
        Assert.IsEmpty(ChartModelComparer.Differences(new EditableChart(480), new EditableChart(480)));
    }

    [TestMethod]
    [DynamicData(nameof(ChangeNames))]
    public void EachKindOfChangeIsReportedWhereItIs(string name)
    {
        var change = EditableChartSamples.ChangeNamed(name);
        var changed = EditableChartSamples.Full();
        change.Apply(changed);

        var differences = ChartModelComparer.Differences(EditableChartSamples.Full(), changed);

        var difference = Assert.ContainsSingle(differences);
        Assert.AreEqual(change.Kind, difference.Kind);
        Assert.AreEqual(change.Part, difference.Part);
        Assert.AreEqual(change.Difficulty, difference.Difficulty);
        Assert.AreEqual(change.Tick, difference.Tick);
        Assert.IsFalse(string.IsNullOrWhiteSpace(difference.Description));
    }

    [TestMethod]
    public void TheDescriptionSaysWhereAndWhat()
    {
        var changed = EditableChartSamples.Full();
        EditableChartSamples.ChangeNamed("SustainChanged").Apply(changed);

        var difference = ChartModelComparer.Differences(EditableChartSamples.Full(), changed).Single();

        Assert.AreEqual("Guitar Expert at tick 480: a note is lane 2, length 120 rather than lane 2, length 240.", difference.Description);
    }

    [TestMethod]
    public void MissingAndExtraItemsAreToldApart()
    {
        var expected = new EditableChart(480);
        expected.AddPart(ChartInstrument.Guitar)[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 0));
        var actual = new EditableChart(480);
        actual.AddPart(ChartInstrument.Guitar)[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 1));

        var differences = ChartModelComparer.Differences(expected, actual);

        Assert.HasCount(2, differences);
        Assert.Contains("missing", differences[0].Description);
        Assert.Contains("shouldn't be there", differences[1].Description);
    }

    [TestMethod]
    public void TheResolutionIsCompared()
    {
        var difference = ChartModelComparer.Differences(new EditableChart(480), new EditableChart(960)).Single();

        Assert.AreEqual(ModelDifferenceKind.Resolution, difference.Kind);
    }

    [TestMethod]
    public void TheOrderOfEventsAtOneTickIsCompared()
    {
        var expected = new EditableChart(480);
        expected.Events.Add(TextEvent.Of(0, "[a]"));
        expected.Events.Add(TextEvent.Of(0, "[b]"));
        var actual = new EditableChart(480);
        actual.Events.Add(TextEvent.Of(0, "[b]"));
        actual.Events.Add(TextEvent.Of(0, "[a]"));

        var difference = ChartModelComparer.Differences(expected, actual).Single();

        Assert.AreEqual(ModelDifferenceKind.GlobalEvent, difference.Kind);
        Assert.AreEqual(0, difference.Tick);
    }

    [TestMethod]
    public void PastTheLimitOneEntrySaysThereWereMore()
    {
        var expected = new EditableChart(480);
        expected.AddPart(ChartInstrument.Guitar);
        var actual = expected.Clone();
        var notes = actual.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes;
        for (int i = 0; i < ChartModelComparer.MaxDifferences + 50; i++)
            notes.Add(new EditableNote(i * 10, 1, 0));

        var differences = ChartModelComparer.Differences(expected, actual);

        Assert.HasCount(ChartModelComparer.MaxDifferences + 1, differences);
        Assert.IsTrue(differences.Take(ChartModelComparer.MaxDifferences).All(d => d.Kind == ModelDifferenceKind.Note));
        Assert.AreEqual(ModelDifferenceKind.More, differences[^1].Kind);
    }

    [TestMethod]
    public void ExactlyAtTheLimitNothingSaysThereWereMore()
    {
        var expected = new EditableChart(480);
        expected.AddPart(ChartInstrument.Guitar);
        var actual = expected.Clone();
        var notes = actual.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes;
        for (int i = 0; i < ChartModelComparer.MaxDifferences; i++)
            notes.Add(new EditableNote(i * 10, 1, 0));

        var differences = ChartModelComparer.Differences(expected, actual);

        Assert.HasCount(ChartModelComparer.MaxDifferences, differences);
        Assert.IsFalse(differences.Any(d => d.Kind == ModelDifferenceKind.More));
    }
}
