using System.Collections.Specialized;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.Resources;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.Tests.Charting.Lint;

/// <summary>
/// The chart issues list (GitHub issue #53): findings in sections by how serious they are, each in the order of its
/// tracks, in one flat list a virtualizing list shows; searched and filtered by chips; changed only where the findings,
/// the search or the filter have.
/// </summary>
[TestClass]
public sealed class ChartIssuesTests
{
    private static readonly Finding Load = new(ChrRules.Chr086, Severity.Info, "Left out", Tick: 0);
    private static readonly Finding Song = new(ChrRules.Chr082, Severity.Info, "No sections");
    private static readonly Finding GuitarExpert = new(ChrRules.Chr004, Severity.Warning, "Off grid", Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 10, 10));
    private static readonly Finding GuitarExpertLater = new(ChrRules.Chr004, Severity.Error, "Off grid", Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 20, 20));
    private static readonly Finding Bass = new(ChrRules.Chr080, Severity.Warning, "Empty", Location: new ChartLocation(ChartInstrument.Bass, null, 0, 0));
    private static readonly Finding Harmony = new("CHR095", Severity.Warning, "Lyric without a note", Tick: 480, Track: "HARM2");

    [TestMethod]
    public void FindingsAreInSectionsErrorsThenWarningsThenInfoEachInTrackOrder()
    {
        var issues = new ChartIssuesViewModel();

        Show(issues, [GuitarExpert, Bass, Song, GuitarExpertLater, Load]);

        Assert.AreSequenceEqual(
            (string[])["Error 1", "Off grid", "Warning 2", "Off grid", "Empty", "Info 2", "Left out", "No sections"],
            Shape(issues));
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

        Assert.AreSame(kept[2], issues.Rows[0], "The warnings' heading is kept.");
        Assert.AreSame(kept[3], issues.Rows[1], "The first guitar finding's row is kept.");
        Assert.AreSame(kept[^1], issues.Rows[^1], "The song-wide finding's row is kept.");
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
    public void TheFooterCountsAreSingularForOne()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass, GuitarExpertLater]);

        Assert.AreEqual(string.Format(SharedStrings.ChartIssuesWarningsFormat, 2), issues.WarningCountText);
        Assert.AreEqual(SharedStrings.ChartIssuesErrorOne, issues.ErrorCountText);
        Assert.AreEqual(string.Format(SharedStrings.ChartIssuesInfosFormat, 0), issues.InfoCountText);
        Assert.IsFalse(issues.IsClean);
    }

    [TestMethod]
    public void ARowsTrackIsItsPartAndDifficultyItsVocalPartTheChartFileOrTheWholeChart()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass, Harmony, Load, Song]);

        Assert.AreSequenceEqual(
            (string[])[
                string.Format(SharedStrings.ChartIssuesTrackFormat, ChartLabels.InstrumentName(ChartInstrument.Guitar), ChartLabels.DifficultyName(ChartDifficulty.Expert)),
                ChartLabels.InstrumentName(ChartInstrument.Bass),
                ChartLabels.VocalTrackName(VocalTrack.Harmony2),
                SharedStrings.ChartIssuesChartFile,
                SharedStrings.ChartIssuesWholeChart],
            issues.Items.Select(i => i.TrackText).ToList());
        Assert.IsTrue(issues.Items[0].IsExpert);
        Assert.IsFalse(issues.Items[1].HasDifficulty);
    }

    [TestMethod]
    public void TheSearchMatchesTheDescriptionRuleTrackAndPlace()
    {
        var issues = new ChartIssuesViewModel(TimeSpan.Zero);
        issues.Show<Finding>(
            [GuitarExpert, Bass, Song],
            static f => f,
            static f => new FindingItem(f, static f => f == GuitarExpert ? "m12 beat 3" : null));

        issues.SearchText = "m12";
        Assert.AreSequenceEqual((string[])["Warning 1", "Off grid"], Shape(issues));

        issues.SearchText = "chr080";
        Assert.AreSequenceEqual((string[])["Warning 1", "Empty"], Shape(issues));

        issues.SearchText = ChartLabels.InstrumentName(ChartInstrument.Guitar).ToUpperInvariant();
        Assert.AreSequenceEqual((string[])["Warning 1", "Off grid"], Shape(issues));

        issues.SearchText = "sections";
        Assert.AreSequenceEqual((string[])["Info 1", "No sections"], Shape(issues));
        Assert.IsTrue(issues.IsNarrowed);
        Assert.AreEqual(1, issues.ShownCount);

        issues.ClearSearchCommand.Execute(null);
        Assert.AreEqual(3, issues.ShownCount);
        Assert.IsFalse(issues.IsNarrowed);
    }

    [TestMethod]
    public void ChipsInAGroupAddToWhatsShownAndGroupsNarrowIt()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass, Song, GuitarExpertLater]);

        issues.ErrorChip.IsOn = true;
        issues.WarningChip.IsOn = true;
        Assert.AreSequenceEqual((string[])["Error 1", "Off grid", "Warning 2", "Off grid", "Empty"], Shape(issues));
        Assert.AreEqual(2, issues.ActiveFilterCount);

        Chip(issues.InstrumentChips, ChartLabels.InstrumentName(ChartInstrument.Bass)).IsOn = true;
        Assert.AreSequenceEqual((string[])["Warning 1", "Empty"], Shape(issues));

        Chip(issues.InstrumentChips, ChartLabels.InstrumentName(ChartInstrument.Guitar)).IsOn = true;
        Chip(issues.DifficultyChips, nameof(ChartDifficulty.Expert)).IsOn = true;
        Assert.AreSequenceEqual((string[])["Error 1", "Off grid", "Warning 1", "Off grid"], Shape(issues), "A finding in no difficulty isn't shown once a difficulty is picked.");
    }

    [TestMethod]
    public void TheFooterCountsEveryFindingWhileTheSectionsCountWhatsShown()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass, GuitarExpertLater]);

        Chip(issues.InstrumentChips, ChartLabels.InstrumentName(ChartInstrument.Bass)).IsOn = true;

        Assert.AreEqual(1, issues.ErrorCount);
        Assert.AreEqual(2, issues.WarningCount);
        Assert.AreEqual(1, issues.Rows.OfType<FindingGroup>().Single().Count);
        Assert.AreEqual(string.Format(SharedStrings.ChartIssuesShownSummaryFormat, 1, 3), issues.ShownSummaryText);
    }

    [TestMethod]
    public void AChipOnStaysWhenItsFindingsGoAndClearingFiltersShowsEverything()
    {
        var issues = new ChartIssuesViewModel(TimeSpan.Zero);
        Show(issues, [GuitarExpert, Bass]);
        string bass = ChartLabels.InstrumentName(ChartInstrument.Bass);
        Chip(issues.InstrumentChips, bass).IsOn = true;
        issues.SearchText = "empty";

        Show(issues, [GuitarExpert]);

        Assert.AreEqual(0, Chip(issues.InstrumentChips, bass).Count);
        Assert.IsTrue(issues.ShowNoMatches);

        issues.ClearFiltersCommand.Execute(null);

        Assert.IsFalse(issues.ShowNoMatches);
        Assert.IsFalse(issues.IsNarrowed);
        Assert.AreEqual(1, issues.ShownCount);
        Assert.IsFalse(issues.InstrumentChips.Any(c => c.Id == bass), "A chip turned off with nothing to match goes.");
    }

    [TestMethod]
    public void AFindingShownAgainAfterTheFilterKeepsItsRow()
    {
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass]);
        var guitar = issues.Rows.OfType<FindingItem>().First();

        issues.InfoChip.IsOn = true;
        issues.InfoChip.IsOn = false;

        Assert.AreSame(guitar, issues.Rows.OfType<FindingItem>().First());
    }

    [TestMethod]
    public void ClearingForAnotherChartClearsTheSearchAndTurnsEveryChipOff()
    {
        var issues = new ChartIssuesViewModel(TimeSpan.Zero);
        Show(issues, [GuitarExpert, Bass]);
        Chip(issues.InstrumentChips, ChartLabels.InstrumentName(ChartInstrument.Guitar)).IsOn = true;
        issues.ErrorChip.IsOn = true;
        issues.SearchText = "grid";

        issues.Clear();
        Show(issues, [Bass]);

        Assert.IsNull(issues.SearchText);
        Assert.IsFalse(issues.IsNarrowed);
        Assert.AreEqual(0, issues.ActiveFilterCount);
        Assert.AreSequenceEqual((string[])["Warning 1", "Empty"], Shape(issues));
        Assert.AreSequenceEqual((string[])[ChartLabels.InstrumentName(ChartInstrument.Bass)], issues.InstrumentChips.Select(c => c.Id).ToList());
    }

    [TestMethod]
    public void AFindingAboutASongIniKeyShowsTheKeyWhereItIsAndTheSearchFindsIt()
    {
        var delay = new Finding("SNG010", Severity.Warning, "Not a number", Key: "delay");
        var issues = new ChartIssuesViewModel(TimeSpan.Zero);
        Show(issues, [delay, Song]);

        Assert.AreEqual("delay", issues.Items[0].PlaceText);
        Assert.AreEqual("", issues.Items[1].PlaceText);

        issues.SearchText = "DELAY";
        Assert.AreSequenceEqual((string[])["Warning 1", "Not a number"], Shape(issues));
    }

    [TestMethod]
    public void WhatTheLoadLeftOutOfAPartIsListedAndFilteredWithThatPart()
    {
        var leftOut = new Finding(ChrRules.Chr086, Severity.Info, "Left out of bass", Location: new ChartLocation(ChartInstrument.Bass, ChartDifficulty.Expert, 0, 0));
        var bassInfo = new Finding(ChrRules.Chr080, Severity.Info, "Bass info", Location: new ChartLocation(ChartInstrument.Bass, ChartDifficulty.Expert, 0, 0));
        var guitarInfo = new Finding(ChrRules.Chr080, Severity.Info, "Guitar info", Location: new ChartLocation(ChartInstrument.Guitar, ChartDifficulty.Expert, 0, 0));
        var issues = new ChartIssuesViewModel();
        Show(issues, [guitarInfo, bassInfo, leftOut, Song, Load]);

        Assert.AreSequenceEqual((string[])["Info 5", "Left out", "No sections", "Guitar info", "Bass info", "Left out of bass"], Shape(issues));

        Chip(issues.InstrumentChips, SharedStrings.ChartIssuesChartFile).IsOn = true;
        Assert.AreSequenceEqual((string[])["Info 1", "Left out"], Shape(issues));
    }

    [TestMethod]
    public void AChipOnWithItsFindingsGoneKeepsItsPlaceAsChipsAreAdded()
    {
        var drums = new Finding(ChrRules.Chr004, Severity.Warning, "Drums", Location: new ChartLocation(ChartInstrument.Drums, ChartDifficulty.Expert, 0, 0));
        var issues = new ChartIssuesViewModel();
        Show(issues, [GuitarExpert, Bass]);
        Chip(issues.InstrumentChips, ChartLabels.InstrumentName(ChartInstrument.Bass)).IsOn = true;

        Show(issues, [GuitarExpert, drums, Harmony]);

        Assert.AreSequenceEqual(
            (string[])[
                ChartLabels.InstrumentName(ChartInstrument.Guitar),
                ChartLabels.InstrumentName(ChartInstrument.Bass),
                ChartLabels.InstrumentName(ChartInstrument.Drums),
                ChartLabels.VocalTrackName(VocalTrack.Harmony2)],
            issues.InstrumentChips.Select(c => c.Id).ToList());
    }

    [TestMethod]
    public void ClearSearchIsOnlyThereWithSomethingToClearSoEscapeCanCloseTheWindow()
    {
        var issues = new ChartIssuesViewModel(TimeSpan.Zero);
        Show(issues, [GuitarExpert]);

        Assert.IsFalse(issues.ClearSearchCommand.CanExecute(null));

        issues.SearchText = "grid";
        Assert.IsTrue(issues.ClearSearchCommand.CanExecute(null));

        issues.SearchText = "  ";
        Assert.IsFalse(issues.ClearSearchCommand.CanExecute(null));
    }

    [TestMethod]
    public void ASearchStillBeingTypedDoesntNarrowTheListYetButCanBeCleared()
    {
        var issues = new ChartIssuesViewModel(TimeSpan.FromMinutes(1));
        Show(issues, [GuitarExpert, Bass]);

        issues.SearchText = "grid";

        Assert.IsFalse(issues.HasSearch, "The list isn't filtered until typing stops.");
        Assert.IsFalse(issues.IsNarrowed);
        Assert.AreEqual(2, issues.ShownCount);
        Assert.IsTrue(issues.ClearFiltersCommand.CanExecute(null));
    }

    [TestMethod]
    public void APlaceIsOneMeasureAndBeatOrARangeWhenItEndsSomewhereElse()
    {
        var start = new MeasureBeat(12, 3);
        string at = string.Format(SharedStrings.FindingPlaceFormat, 12, 3.0);

        Assert.AreEqual(at, ChartLabels.PlaceText(start));
        Assert.AreEqual(at, ChartLabels.PlaceText(start, start));
        Assert.AreEqual(at, ChartLabels.PlaceText(start, new MeasureBeat(12, 3.001)), "Ends that read the same are one place.");
        Assert.AreEqual(
            string.Format(SharedStrings.FindingPlaceRangeFormat, at, string.Format(SharedStrings.FindingPlaceFormat, 14, 1.0)),
            ChartLabels.PlaceText(start, new MeasureBeat(14, 1)));
    }

    [TestMethod]
    public void AnyTrackOtherThanAVocalPartIsNamedAsItIs()
    {
        Assert.AreEqual(ChartLabels.VocalTrackName(VocalTrack.Vocals), ChartLabels.TrackName(" part vocals "));
        Assert.AreEqual("EVENTS", ChartLabels.TrackName("EVENTS"));
        Assert.IsNull(ChartLabels.TrackName(null));
        Assert.IsNull(ChartLabels.TrackName(" "));
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

    // Each heading as its severity and count, each finding as its message.
    private static List<string> Shape(ChartIssuesViewModel issues) =>
        [.. issues.Rows.Select(r => r is FindingGroup g ? $"{g.Severity} {g.Count}" : ((FindingItem)r).Message)];

    private static CountedFilterChipViewModel Chip(IEnumerable<CountedFilterChipViewModel> chips, string id) => chips.Single(c => c.Id == id);
}
