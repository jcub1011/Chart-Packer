using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// A highway lane in perspective: <c>V</c> steps a lane through a flat highway, perspective and a timeline, and the choice is
/// kept with the lane, over the setting's default, which is read from a temp folder.
/// </summary>
[TestClass]
public sealed class PerspectiveLaneTests
{
    private readonly List<PlaybackViewModel> _playbacks = [];
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-perspective-");

    [TestCleanup]
    public void DeleteRoot()
    {
        foreach (var playback in _playbacks)
            playback.Dispose();
        _root.Delete(recursive: true);
    }

    private string SettingsPath => Path.Combine(_root.FullName, "builder-settings.json");

    private sealed class NoConfirmations : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? confirmIcon = null) => Task.FromResult(false);

        public Task<ConfirmChoice> ChooseAsync(string title, string message, string firstLabel, string secondLabel, string? firstIcon = null, string? secondIcon = null) => Task.FromResult(ConfirmChoice.Cancel);

        public Task AlertAsync(string title, string message) => Task.CompletedTask;
    }

    private (ChartEditorViewModel Editor, BuilderSettingsService Settings, LaneDockLayout Layout) Make(bool perspectiveByDefault = false)
    {
        var settings = BuilderSettingsService.Load(SettingsPath);
        settings.Settings.DefaultEditorPerspective = perspectiveByDefault;
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums }, DrumsVariant.Pro));
        var song = EditorSong.New(chart, []);
        var playback = new PlaybackViewModel(song, null, null);
        _playbacks.Add(playback);
        var layout = LaneDockLayout.CreateDefault();
        var editor = new ChartEditorViewModel(song, new ChartUndoStack(chart), playback, new EditorAudio(song, settings.Settings), settings, new NoConfirmations(), new EditorMessages(), layout);
        return (editor, settings, layout);
    }

    private static EditorLaneViewModel HighwayLane(ChartEditorViewModel editor, int index = 0) => editor.Lanes.All.Where(l => !l.IsVocal).ElementAt(index);

    [TestMethod]
    public void ALaneOpensAsAFlatHighwayUnlessThePerspectiveDefaultIsOn()
    {
        var flat = HighwayLane(Make().Editor);
        Assert.IsTrue(flat.IsFlatHighway);
        Assert.IsFalse(flat.IsPerspective);
        Assert.IsFalse(flat.IsTimeline);

        var tilted = HighwayLane(Make(perspectiveByDefault: true).Editor);
        Assert.IsTrue(tilted.IsPerspective);
        Assert.IsFalse(tilted.IsFlatHighway);
    }

    [TestMethod]
    public void VStepsALaneFromAFlatHighwayToPerspectiveToATimelineAndBack()
    {
        var lane = HighwayLane(Make().Editor);

        var seen = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            seen.Add($"{(lane.IsTimeline ? "timeline" : lane.IsPerspective ? "perspective" : "flat")} -> {lane.ViewToolTip}");
            lane.ToggleView();
        }

        Assert.AreSequenceEqual(
            new[]
            {
                $"flat -> {Strings.LaneShowPerspectiveToolTip}",
                $"perspective -> {Strings.LaneShowTimelineToolTip}",
                $"timeline -> {Strings.LaneShowHighwayToolTip}",
                $"flat -> {Strings.LaneShowPerspectiveToolTip}",
            },
            seen,
            "Each step's button says where the next goes, and the cycle comes round.");
    }

    [TestMethod]
    public void OnlyAHighwayCanBeInPerspective()
    {
        var lane = HighwayLane(Make(perspectiveByDefault: true).Editor);
        Assert.IsTrue(lane.IsPerspective);

        lane.View = LaneView.Timeline;

        Assert.IsFalse(lane.IsPerspective, "A timeline has no perspective, whatever the lane remembers.");
        Assert.IsFalse(lane.IsFlatHighway);
        Assert.IsTrue(lane.IsTimeline);
    }

    [TestMethod]
    public void ALanesChoiceIsKeptWithItsLayoutAndAnUntouchedLaneFollowsTheDefault()
    {
        var (editor, settings, layout) = Make(perspectiveByDefault: true);
        var chosen = HighwayLane(editor, 0);
        var followed = HighwayLane(editor, 1);

        Assert.IsNull(layout.Find(chosen.Lane.Id)!.Perspective, "Opened on the default, it isn't kept as the lane's own.");

        // From perspective to the timeline: the lane's own is not perspective, though the default is.
        chosen.ToggleView();
        Assert.IsTrue(chosen.IsTimeline);
        Assert.AreEqual(false, layout.Find(chosen.Lane.Id)!.Perspective);

        settings.Settings.DefaultEditorPerspective = false;
        editor.ReloadSettings();
        Assert.IsFalse(followed.IsPerspective, "A lane that has made no choice follows the setting.");
        Assert.IsNull(layout.Find(followed.Lane.Id)!.Perspective, "And following it isn't a choice either.");

        settings.Settings.DefaultEditorPerspective = true;
        editor.ReloadSettings();
        Assert.IsTrue(followed.IsPerspective);
        Assert.IsFalse(chosen.Perspective, "A lane that has chosen keeps its choice.");
    }

    [TestMethod]
    public void ALanesPerspectiveSurvivesTheLayoutBeingSavedAndRead()
    {
        var (editor, _, layout) = Make();
        var lane = HighwayLane(editor, 1);
        lane.ToggleView();
        Assert.IsTrue(lane.IsPerspective);

        var read = LaneDockLayout.FromState(layout.ToState())!;

        Assert.AreEqual(true, read.Find(lane.Lane.Id)?.Perspective ?? read.Lanes.ElementAt(1).Perspective);
    }

    [TestMethod]
    public void TheSettingIsOffByDefaultAndReadsBack()
    {
        Assert.IsFalse(BuilderSettingsService.Load(SettingsPath).Settings.DefaultEditorPerspective);

        var service = BuilderSettingsService.Load(SettingsPath);
        service.Settings.DefaultEditorPerspective = true;
        service.Save();

        Assert.IsTrue(BuilderSettingsService.Load(SettingsPath).Settings.DefaultEditorPerspective);
    }

    [TestMethod]
    public void AFileSavedBeforeTheSettingStillLoadsWithItOff()
    {
        File.WriteAllText(SettingsPath, """{ "DefaultEditorView": 0, "EditorHighwaySpeed": 1.5 }""");

        var settings = BuilderSettingsService.Load(SettingsPath).Settings;

        Assert.IsFalse(settings.DefaultEditorPerspective);
        Assert.AreEqual(1.5, settings.EditorHighwaySpeed, 1e-9, "What it did have is kept.");
    }
}
