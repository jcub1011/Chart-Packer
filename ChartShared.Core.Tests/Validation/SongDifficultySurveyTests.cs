using System.Text;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Validation;

/// <summary>
/// A song's chart surveyed once for a batch of lower difficulties (issue #120): each part's charted difficulties, planned
/// again in memory as the batch's scope changes, and why a song can't be surveyed.
/// </summary>
[TestClass]
public sealed class SongDifficultySurveyTests
{
    private static readonly HashSet<ChartDifficulty> Lower = [ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy];

    private DirectoryInfo _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-survey-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void EachPartsChartedDifficultiesAreKept()
    {
        var survey = SongDifficultySurvey.Of(GuitarAndDrums());

        Assert.AreEqual(SongSurveyStatus.Ok, survey.Status);
        Assert.AreSequenceEqual([ChartInstrument.Guitar, ChartInstrument.Drums], [.. survey.Parts.Select(p => p.Part)]);
        Assert.IsTrue(survey.Parts[0].Charted.SetEquals([ChartDifficulty.Expert, ChartDifficulty.Medium]));
        Assert.IsTrue(survey.Parts[1].Charted.SetEquals([ChartDifficulty.Expert]));
    }

    [TestMethod]
    public void APlanMarksTheDifficultiesItMakesAgain()
    {
        var survey = SongDifficultySurvey.Of(GuitarAndDrums());

        var skip = survey.Plan(LowerDifficultyScope.Missing);
        var replace = survey.Plan(new LowerDifficultyScope(Lower, null, Replace: true));

        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Easy], skip[0].Targets);
        Assert.IsFalse(skip[0].Replaces(ChartDifficulty.Medium));
        Assert.AreSequenceEqual([ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy], replace[0].Targets);
        Assert.IsTrue(replace[0].Replaces(ChartDifficulty.Medium));
        Assert.IsFalse(replace[0].Replaces(ChartDifficulty.Hard));
    }

    [TestMethod]
    public void APlanLeavesOutThePartsNotPicked()
    {
        var survey = SongDifficultySurvey.Of(GuitarAndDrums());

        var plan = survey.Plan(new LowerDifficultyScope(Lower, new HashSet<ChartInstrument> { ChartInstrument.Drums }, Replace: false));

        Assert.AreSequenceEqual([ChartInstrument.Drums], [.. plan.Select(p => p.Part)]);
    }

    [TestMethod]
    public async Task ASongsChartIsSurveyedAsDrafted()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(GuitarAndDrums())));

        var survey = await SurveyAsync(entry, null);

        Assert.AreEqual(SongSurveyStatus.Ok, survey.Status);
        Assert.HasCount(2, survey.Parts);
    }

    [TestMethod]
    public async Task ASongWhoseDraftChangesItsChartOtherwiseIsNotSurveyed()
    {
        var entry = Folder(("notes.mid", MidiChartWriter.Write(GuitarAndDrums())));
        var draft = new MetadataDraft();
        draft.SetChartOffset(new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Expert), 10);

        var survey = await SurveyAsync(entry, draft);

        Assert.AreEqual(SongSurveyStatus.ChartEdits, survey.Status);
        Assert.IsEmpty(survey.Parts);
    }

    [TestMethod]
    public async Task ASongWithoutAChartSaysSo()
    {
        var entry = Folder(("song.ogg", [0]));

        var survey = await SurveyAsync(entry, null);

        Assert.AreEqual(SongSurveyStatus.NoChart, survey.Status);
    }

    [TestMethod]
    public async Task AMalformedChartIsReportedNotThrown()
    {
        var entry = Folder(("notes.mid", Encoding.ASCII.GetBytes("not a midi file")));

        var survey = await SurveyAsync(entry, null);

        Assert.AreEqual(SongSurveyStatus.Unreadable, survey.Status);
        Assert.IsNotNull(survey.Error);
    }

    [TestMethod]
    public async Task AChartTooLargeToReadSaysSo()
    {
        var entry = Folder(("notes.mid", []));
        using (var stream = File.OpenWrite(Path.Combine(entry.FullPath, "notes.mid")))
            stream.SetLength(ChartReader.MaxBytes + 1);

        var survey = await SurveyAsync(entry, null);

        Assert.AreEqual(SongSurveyStatus.ChartTooLarge, survey.Status);
    }

    // Guitar on Expert and Medium, drums on Expert alone.
    private static EditableChart GuitarAndDrums()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [.. Enumerable.Range(0, 8).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Medium, [new EditableNote(0, 1, 0)]).Apply(chart);
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [.. Enumerable.Range(0, 8).Select(i => new EditableNote(i * 480L, 1, i % 4))]).Apply(chart);
        return chart;
    }

    private async Task<SongDifficultySurvey> SurveyAsync(SongEntry entry, MetadataDraft? draft)
    {
        var (report, files) = await SongMetadataCheck.OpenAsync(entry, TestContext.CancellationToken);
        Assert.IsNotNull(files);
        using (files)
            return await SongDifficultySurvey.ReadAsync(files, report.DraftedPairs(draft), draft, TestContext.CancellationToken);
    }

    private SongEntry Folder((string Name, byte[] Data) file)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "Song")).FullName;
        File.WriteAllBytes(Path.Combine(folder, file.Name), file.Data);
        File.WriteAllBytes(Path.Combine(folder, "song.ini"), Encoding.UTF8.GetBytes("[song]\nname = Song\n"));
        return new SongEntry(SongEntryKind.SongFolder, folder, Path.GetRelativePath(_root.FullName, folder), Path.GetFileName(folder));
    }
}
