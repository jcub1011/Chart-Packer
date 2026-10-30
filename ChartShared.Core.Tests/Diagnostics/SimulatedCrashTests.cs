using ChartShared.Core.Diagnostics;
using ChartShared.Core.Launching;

namespace ChartShared.Core.Tests.Diagnostics;

[TestClass]
public sealed class SimulatedCrashTests
{
    [TestMethod]
    public void WithoutTheOptionTheArgumentsAreKept()
    {
        var rest = SimulatedCrash.Take(["--new", "song.ogg"], out bool requested);

        Assert.IsFalse(requested);
        Assert.AreSequenceEqual(new[] { "--new", "song.ogg" }, [.. rest]);
    }

    [TestMethod]
    public void TheOptionIsTakenOutWhereverItIsAmongTheOptions()
    {
        var rest = SimulatedCrash.Take(["--new", "--crash", "song.ogg", "--crash"], out bool requested);

        Assert.IsTrue(requested);
        Assert.AreSequenceEqual(new[] { "--new", "song.ogg" }, [.. rest]);
    }

    [TestMethod]
    public void AfterEndOfOptionsItIsAPath()
    {
        var rest = SimulatedCrash.Take(["--", "--crash"], out bool requested);

        Assert.IsFalse(requested);
        Assert.AreSequenceEqual(new[] { "--", "--crash" }, [.. rest]);
    }

    [TestMethod]
    public void ChartBuilderReadsWhatIsLeft()
    {
        var args = BuilderArgs.Parse(SimulatedCrash.Take(["--crash", "Songs"], out bool requested));

        Assert.IsTrue(requested);
        Assert.IsNull(args.Problem);
        Assert.AreEqual(Path.GetFullPath("Songs"), args.SongPath);
    }
}
