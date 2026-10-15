using ChartPacker.Core.Launching;

namespace ChartPacker.Core.Tests.Launching;

[TestClass]
public sealed class BuilderArgsTests
{
    [TestMethod]
    public void NoArgumentsOpenTheHomeScreen()
    {
        var args = BuilderArgs.Parse([]);

        Assert.AreEqual(BuilderStart.Home, args.Start);
        Assert.IsNull(args.Problem);
    }

    [TestMethod]
    public void APathOpensThatSongAsAFullPath()
    {
        var args = BuilderArgs.Parse([Path.Combine("Songs", "Artist - Title")]);

        Assert.AreEqual(BuilderStart.OpenSong, args.Start);
        Assert.AreEqual(Path.GetFullPath(Path.Combine("Songs", "Artist - Title")), args.SongPath);
    }

    [TestMethod]
    public void EndOfOptionsLetsASongPathLookLikeAnOption()
    {
        var args = BuilderArgs.Parse(["--", "--draft song"]);

        Assert.AreEqual(BuilderStart.OpenSong, args.Start);
        Assert.AreEqual(Path.GetFullPath("--draft song"), args.SongPath);
    }

    [TestMethod]
    public void NewTakesAudioFilesAsFullPaths()
    {
        var args = BuilderArgs.Parse(["--new", "guitar.ogg", Path.Combine("stems", "drums.ogg")]);

        Assert.AreEqual(BuilderStart.NewChart, args.Start);
        Assert.AreSequenceEqual(
            new[] { Path.GetFullPath("guitar.ogg"), Path.GetFullPath(Path.Combine("stems", "drums.ogg")) }, [.. args.AudioFiles]);
    }

    [TestMethod]
    public void AfterEndOfOptionsEverythingIsAFile()
    {
        var args = BuilderArgs.Parse(["--new", "--", "--new", "-song.ogg"]);

        Assert.AreSequenceEqual(new[] { Path.GetFullPath("--new"), Path.GetFullPath("-song.ogg") }, [.. args.AudioFiles]);
    }

    [TestMethod]
    public void CalibrateOpensTheCalibrationTool() =>
        Assert.AreEqual(BuilderStart.Calibrate, BuilderArgs.Parse(["--calibrate"]).Start);

    [TestMethod]
    [DataRow(new[] { "--edit" }, BuilderArgsProblemKind.UnknownOption, "--edit")]
    [DataRow(new[] { "--new", "--open", "a.ogg" }, BuilderArgsProblemKind.UnknownOption, "--open")]
    [DataRow(new[] { "--parent", "a" }, BuilderArgsProblemKind.UnknownOption, "--parent")]
    [DataRow(new[] { "--new", "" }, BuilderArgsProblemKind.UnexpectedArgument, "")]
    [DataRow(new[] { "--calibrate", "now" }, BuilderArgsProblemKind.UnexpectedArgument, "now")]
    [DataRow(new[] { "one", "two" }, BuilderArgsProblemKind.UnexpectedArgument, "two")]
    [DataRow(new[] { "--", "one", "two" }, BuilderArgsProblemKind.UnexpectedArgument, "two")]
    [DataRow(new[] { "--" }, BuilderArgsProblemKind.UnexpectedArgument, "--")]
    [DataRow(new[] { " " }, BuilderArgsProblemKind.UnexpectedArgument, " ")]
    public void BadArgumentsOpenTheHomeScreenWithTheProblem(string[] arguments, BuilderArgsProblemKind kind, string argument)
    {
        var args = BuilderArgs.Parse(arguments);

        Assert.AreEqual(BuilderStart.Home, args.Start);
        Assert.AreEqual(new BuilderArgsProblem(kind, argument), args.Problem);
    }

    [TestMethod]
    public void SongArgumentsReadBackAsAFullPath()
    {
        var args = BuilderArgs.Parse(BuilderArgs.ForSong("--song"));

        Assert.AreEqual(BuilderStart.OpenSong, args.Start);
        Assert.AreEqual(Path.GetFullPath("--song"), args.SongPath);
    }

    [TestMethod]
    public void CalibrateArgumentsReadBack() =>
        Assert.AreEqual(BuilderStart.Calibrate, BuilderArgs.Parse(BuilderArgs.ForCalibrate()).Start);
}
