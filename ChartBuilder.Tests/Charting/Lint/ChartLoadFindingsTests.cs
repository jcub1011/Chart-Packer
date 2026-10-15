using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using static ChartBuilder.Tests.Charting.MidiChartLoaderTests;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>The chart's load issues listed as findings (CHR086), beside the lint findings.</summary>
[TestClass]
public sealed class ChartLoadFindingsTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    [TestMethod]
    public void EveryKindOfIssueIsAnInfoFindingWithAMessage()
    {
        foreach (var kind in Enum.GetValues<ChartLoadIssueKind>())
        {
            var finding = ChartLoadFindings.Of([new ChartLoadIssue(kind, Guitar, Expert, 480)]).Single();

            Assert.AreEqual(ChrRules.Chr086, finding.RuleId, kind.ToString());
            Assert.AreEqual(Severity.Info, finding.Severity, kind.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Message), kind.ToString());
        }
    }

    [TestMethod]
    public void AnIssueOnAPartHasItsLocationAndOneOnTheTempoTrackItsTick()
    {
        var findings = ChartLoadFindings.Of(
        [
            new ChartLoadIssue(ChartLoadIssueKind.DuplicateNote, Guitar, Expert, 960, 96),
            new ChartLoadIssue(ChartLoadIssueKind.UnholdableTempo, null, null, 480),
        ]);

        // Song-wide first.
        Assert.HasCount(2, findings);
        Assert.IsNull(findings[0].Location);
        Assert.AreEqual(480L, findings[0].Tick);
        Assert.AreEqual(new ChartLocation(Guitar, Expert, 960, 960), findings[1].Location);
        Assert.IsNull(findings[1].Tick);
        Assert.Contains("96", findings[1].Message);
    }

    [TestMethod]
    public void MergingKeepsTheOrderFindingsAreShownIn()
    {
        var lint = new[]
        {
            new Finding(ChrRules.Chr082, Severity.Info, "no sections", Tick: 0),
            new Finding(ChrRules.Chr020, Severity.Warning, "cut short", Location: new ChartLocation(Guitar, Expert, 0, 480)),
            new Finding(ChrRules.Chr020, Severity.Warning, "cut short", Location: new ChartLocation(Guitar, Expert, 1920, 2400)),
        };
        var load = ChartLoadFindings.Of(
        [
            new ChartLoadIssue(ChartLoadIssueKind.UnusedMarker, Guitar, Expert, 960),
            new ChartLoadIssue(ChartLoadIssueKind.UnholdableTimeSignature, null, null, 0),
        ]);

        var merged = ChartLoadFindings.Merge(lint, load);

        Assert.AreSequenceEqual(
            [ChrRules.Chr082, ChrRules.Chr086, ChrRules.Chr020, ChrRules.Chr086, ChrRules.Chr020],
            merged.Select(f => f.RuleId).ToList());
        Assert.AreSame(lint, ChartLoadFindings.Merge(lint, []));
    }

    [TestMethod]
    public void ADuplicateNoteInAFileIsListed()
    {
        var result = Load(Song()
            .Track("PART GUITAR")
            .Raw(0, 0x90, 96, 100).Raw(0, 0x91, 96, 100).Raw(50, 0x81, 96, 0).Raw(100, 0x80, 96, 0));

        var finding = ChartLoadFindings.Of(result.Issues).Single();

        Assert.AreEqual(ChrRules.Chr086, finding.RuleId);
        Assert.AreEqual(new ChartLocation(Guitar, Expert, 0, 0), finding.Location);
    }
}
