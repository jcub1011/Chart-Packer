using ChartBuilder.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The 3, 2, 1 before record mode or a playtest starts the song, and the setting for it, always in a temp folder.</summary>
[TestClass]
public sealed class PlaybackCountdownTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-countdown-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileCountsDownForBoth()
    {
        Assert.AreEqual(PlaybackCountdownMode.Both, BuilderSettingsService.Load(_path).Settings.PlaybackCountdown);
    }

    [TestMethod]
    public void AFileWithoutTheSettingCountsDownForBoth()
    {
        File.WriteAllText(_path, """{ "WheelScrollDirection": 1 }""");

        Assert.AreEqual(PlaybackCountdownMode.Both, BuilderSettingsService.Load(_path).Settings.PlaybackCountdown);
    }

    [TestMethod]
    public void AnUnknownModeGoesBackToBoth()
    {
        File.WriteAllText(_path, """{ "PlaybackCountdown": 9 }""");

        Assert.AreEqual(PlaybackCountdownMode.Both, BuilderSettingsService.Load(_path).Settings.PlaybackCountdown);
    }

    [TestMethod]
    [DataRow(PlaybackCountdownMode.Off)]
    [DataRow(PlaybackCountdownMode.Record)]
    [DataRow(PlaybackCountdownMode.Play)]
    [DataRow(PlaybackCountdownMode.Both)]
    public void EachModeReadsBack(PlaybackCountdownMode mode)
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.PlaybackCountdown = mode;
        service.Save();

        Assert.AreEqual(mode, BuilderSettingsService.Load(_path).Settings.PlaybackCountdown);
    }

    [TestMethod]
    [DataRow(PlaybackCountdownMode.Off, false, false, false)]
    [DataRow(PlaybackCountdownMode.Off, true, false, false)]
    [DataRow(PlaybackCountdownMode.Off, false, true, false)]
    [DataRow(PlaybackCountdownMode.Record, true, false, true)]
    [DataRow(PlaybackCountdownMode.Record, false, true, false)]
    [DataRow(PlaybackCountdownMode.Play, true, false, false)]
    [DataRow(PlaybackCountdownMode.Play, false, true, true)]
    [DataRow(PlaybackCountdownMode.Both, true, false, true)]
    [DataRow(PlaybackCountdownMode.Both, false, true, true)]
    public void ItCountsDownForWhatTheModeCoversAndIsArmed(PlaybackCountdownMode mode, bool record, bool playtest, bool expected)
    {
        Assert.AreEqual(expected, PlaybackCountdown.ShouldRun(mode, record, playtest));
    }

    [TestMethod]
    [DataRow(PlaybackCountdownMode.Off)]
    [DataRow(PlaybackCountdownMode.Record)]
    [DataRow(PlaybackCountdownMode.Play)]
    [DataRow(PlaybackCountdownMode.Both)]
    public void PlayingNormallyNeverCountsDown(PlaybackCountdownMode mode)
    {
        Assert.IsFalse(PlaybackCountdown.ShouldRun(mode, recordArmed: false, playtestArmed: false));
    }

    [TestMethod]
    public void ItCountsThreeTwoOneThenIsDone()
    {
        var countdown = new PlaybackCountdown();
        var shown = new List<int> { countdown.Remaining };

        while (!countdown.Tick())
            shown.Add(countdown.Remaining);

        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, shown);
        Assert.IsTrue(countdown.IsDone);
        Assert.AreEqual(0, countdown.Remaining);
    }

    [TestMethod]
    public void ItIsNotDoneBeforeTheLastSecond()
    {
        var countdown = new PlaybackCountdown();

        Assert.IsFalse(countdown.IsDone);
        Assert.IsFalse(countdown.Tick());
        Assert.IsFalse(countdown.Tick());
        Assert.IsTrue(countdown.Tick());
    }

    [TestMethod]
    public void TickingPastTheEndStaysDone()
    {
        var countdown = new PlaybackCountdown();
        for (int i = 0; i < BuilderSettings.DefaultCountdownSeconds; i++)
            countdown.Tick();

        Assert.IsTrue(countdown.Tick());
        Assert.AreEqual(0, countdown.Remaining);
    }

    [TestMethod]
    public void AMissingFileCountsThreeSeconds()
    {
        Assert.AreEqual(3, BuilderSettingsService.Load(_path).Settings.CountdownSeconds);
    }

    [TestMethod]
    [DataRow(0, BuilderSettings.MinCountdownSeconds)]
    [DataRow(-5, BuilderSettings.MinCountdownSeconds)]
    [DataRow(99, BuilderSettings.MaxCountdownSeconds)]
    [DataRow(5, 5)]
    public void TheLengthIsKeptWithinWhatSettingsAllows(int saved, int expected)
    {
        File.WriteAllText(_path, $$"""{ "CountdownSeconds": {{saved}} }""");

        Assert.AreEqual(expected, BuilderSettingsService.Load(_path).Settings.CountdownSeconds);
    }

    [TestMethod]
    public void TheLengthReadsBack()
    {
        var service = BuilderSettingsService.Load(_path);
        service.Settings.CountdownSeconds = 7;
        service.Save();

        Assert.AreEqual(7, BuilderSettingsService.Load(_path).Settings.CountdownSeconds);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(10)]
    public void ItCountsDownFromTheLengthToOne(int seconds)
    {
        var countdown = new PlaybackCountdown(seconds);
        var shown = new List<int> { countdown.Remaining };

        while (!countdown.Tick())
            shown.Add(countdown.Remaining);

        CollectionAssert.AreEqual(Enumerable.Range(1, seconds).Reverse().ToList(), shown);
    }

    [TestMethod]
    public void ALengthOutsideTheRangeIsKept()
    {
        Assert.AreEqual(BuilderSettings.MaxCountdownSeconds, new PlaybackCountdown(500).Remaining);
        Assert.AreEqual(BuilderSettings.MinCountdownSeconds, new PlaybackCountdown(0).Remaining);
    }

    [TestMethod]
    [DataRow(-1, BuilderSettings.MinCountdownSeconds)]
    [DataRow(0, BuilderSettings.MinCountdownSeconds)]
    [DataRow(4, 4)]
    [DataRow(BuilderSettings.MaxCountdownSeconds + 1, BuilderSettings.MaxCountdownSeconds)]
    public void ClampingKeepsTheLengthWithinWhatSettingsAllows(int seconds, int expected)
    {
        Assert.AreEqual(expected, BuilderSettings.ClampCountdownSeconds(seconds));
    }

    [TestMethod]
    public void ARecordModeCountdownIgnoresAnArmedPlaytest()
    {
        Assert.IsFalse(PlaybackCountdown.ShouldRun(PlaybackCountdownMode.Record, recordArmed: false, playtestArmed: true));
        Assert.IsFalse(PlaybackCountdown.ShouldRun(PlaybackCountdownMode.Play, recordArmed: true, playtestArmed: false));
    }

    // A run counting for as long as `wanted` says, noting what it showed and how often it started the song.
    private sealed class RunProbe
    {
        public bool Wanted = true;
        public readonly List<int> Shown = [];
        public int Starts;
        public PlaybackCountdownRun Run { get; }

        public RunProbe(int seconds = 3) =>
            Run = new PlaybackCountdownRun(seconds, () => Wanted, Shown.Add, () => Starts++);
    }

    [TestMethod]
    public void ARunShowsEachNumberThenStartsTheSongOnce()
    {
        var probe = new RunProbe();
        Assert.AreEqual(3, probe.Run.Remaining);

        probe.Run.Tick();
        probe.Run.Tick();
        Assert.AreEqual(0, probe.Starts);
        Assert.IsTrue(probe.Run.IsActive);

        probe.Run.Tick();

        CollectionAssert.AreEqual(new[] { 2, 1 }, probe.Shown);
        Assert.AreEqual(1, probe.Starts);
        Assert.IsFalse(probe.Run.IsActive);
    }

    [TestMethod]
    public void ARunNoLongerWantedPartwayEndsWithoutStarting()
    {
        var probe = new RunProbe();
        probe.Run.Tick();

        probe.Wanted = false;
        probe.Run.Tick();
        probe.Wanted = true;
        probe.Run.Tick();
        probe.Run.Tick();

        Assert.AreEqual(0, probe.Starts);
        Assert.IsFalse(probe.Run.IsActive);
        CollectionAssert.AreEqual(new[] { 2 }, probe.Shown);
    }

    [TestMethod]
    public void ARunNoLongerWantedAtTheLastSecondDoesNotStartTheSong()
    {
        var probe = new RunProbe();
        probe.Run.Tick();
        probe.Run.Tick();

        probe.Wanted = false;
        probe.Run.Tick();

        Assert.AreEqual(0, probe.Starts);
        Assert.IsFalse(probe.Run.IsActive);
    }

    [TestMethod]
    public void ACancelledRunNeverStartsOrShowsAgain()
    {
        var probe = new RunProbe();
        probe.Run.Tick();

        probe.Run.Cancel();
        probe.Run.Tick();
        probe.Run.Tick();

        Assert.IsFalse(probe.Run.IsActive);
        Assert.AreEqual(0, probe.Starts);
        CollectionAssert.AreEqual(new[] { 2 }, probe.Shown);
    }

    [TestMethod]
    public void AFinishedRunDoesNotStartTheSongAgain()
    {
        var probe = new RunProbe(seconds: 1);

        probe.Run.Tick();
        probe.Run.Tick();
        probe.Run.Tick();

        Assert.AreEqual(1, probe.Starts);
        Assert.AreEqual(0, probe.Shown.Count);
    }
}
