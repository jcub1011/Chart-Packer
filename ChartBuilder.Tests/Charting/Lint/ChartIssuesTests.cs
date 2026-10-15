using System.Collections.Specialized;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>
/// The chart issues list (GitHub issue #53): findings grouped by reading the chart file, the chart as a whole, then each
/// part and difficulty, in one flat list a virtualizing list shows; changed only where the findings have.
/// </summary>
[TestClass]
public sealed class ChartIssuesTests
{
    private static readonly Finding Load = new(ChrRules.Chr086, Severity.Info, "Left out", Tick: 0);
    private static readonly Finding Song = new(ChrRules.Chr082, Severity.Info, "No sections");
    private static readonly Finding GuitarExpert = new(ChrRules.Chr004, Severity.Warning, "Off grid", Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 10, 10));
    private static readonly Finding GuitarExpertLater = new(ChrRules.Chr004, Severity.Error, "Off grid", Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 20, 20));
    private static readonly Finding Bass = new(ChrRules.Chr080, Severity.Warning, "Empty", Location: new ChartLocation(ChartInstrument.Bass, null, 0, 0));

    [TestMethod]
    public void FindingsAreGroupedParsingThenTheWholeChartThenEachTrack()
    {
        var issues = new ChartIssuesViewModel();

        Show(issues, [GuitarExpert, Bass, Song, GuitarExpertLater, Load]);

        var shape = issues.Rows.Select(r => r is FindingGroup g ? g.Key.Kind.ToString() + (g.Key.Part?.ToString() ?? "") : ((FindingItem)r).Finding.Message).ToList();
        Assert.AreSequenceEqual(
            (string[])["Parsing", "Left out", "General", "No sections", "PartGuitar", "Off grid", "Off grid", "PartBass", "Empty"],
            shape);
        Assert.AreEqual(1, issues.ErrorCount);
        Assert.AreEqual(2, issues.WarningCount);
        Assert.AreEqual(2, issues.InfoCount);
    }

    [TestMethod]
    public void AFindingStillFoundKeepsItsRowAndOnlyWhatChangedIsReplaced()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [Song, GuitarExpert, GuitarExpertLater, Bass]);
        var kept = issues.Rows.ToList();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        issues.Rows.CollectionChanged += (_, e) => changes.Add(e);

        Show(issues, [Song, GuitarExpert, Bass]);

        Assert.AreSame(kept[1], issues.Rows[1], "The song-wide finding's row is kept.");
        Assert.AreSame(kept[3], issues.Rows[3], "The first guitar finding's row is kept.");
        Assert.AreSame(kept[^1], issues.Rows[^1], "The bass finding's row is kept.");
        Assert.HasCount(1, changes);
        Assert.AreEqual(NotifyCollectionChangedAction.Remove, changes[0].Action);
    }

    [TestMethod]
    public void TheSameFindingsChangeNothing()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [Song, GuitarExpert, Bass]);
        int changes = 0;
        issues.Rows.CollectionChanged += (_, _) => changes++;

        Show(issues, [Song, GuitarExpert, Bass]);

        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void WithoutReuseEveryRowIsMadeAgain()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert]);
        var before = issues.Items[0];

        issues.Show<Finding>([GuitarExpert], static f => f, static f => new FindingItem(f, static _ => null), reuse: false);

        Assert.AreNotSame(before, issues.Items[0]);
    }

    [TestMethod]
    public void TheHeadlineCountsWarningsAndErrorsInTheFile()
    {
        var issues = new ChartIssuesViewModel { FileName = "notes.mid" };
        Show(issues, [GuitarExpert, GuitarExpertLater]);

        Assert.Contains("notes.mid", issues.Headline);
        Assert.Contains("1", issues.Headline);
        Assert.IsTrue(issues.HasErrors);
    }

    [TestMethod]
    public void RowsChangeOnlyBetweenWhatsTheSameAtEitherEnd()
    {
        object a = new(), b = new(), c = new(), d = new(), e = new();
        var rows = new Avalonia.Collections.AvaloniaList<object> { a, b, c, d };
        var changes = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, args) => changes.Add(args);

        ListRows.Replace(rows, [a, e, d]);

        Assert.AreSequenceEqual((object[])[a, e, d], rows.ToList());
        Assert.HasCount(2, changes);
        Assert.AreEqual(1, changes[0].OldStartingIndex);
        Assert.HasCount(2, changes[0].OldItems ?? Array.Empty<object>());
        Assert.AreEqual(1, changes[1].NewStartingIndex);
    }

    private static void Show(ChartIssuesViewModel issues, IReadOnlyList<Finding> findings) =>
        issues.Show(findings, static f => f, static f => new FindingItem(f, static _ => null));
}
