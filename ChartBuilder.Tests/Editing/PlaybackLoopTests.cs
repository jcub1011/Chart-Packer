using ChartBuilder.ViewModels;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The loop region as playback holds it (charter-spec C10 item 2): setting it, or dragging it, doesn't start the song
/// repeating it, which the Loop button and a tap of L do; clearing it stops repeating.
/// </summary>
[TestClass]
public sealed class PlaybackLoopTests
{
    [TestMethod]
    public void SettingTheRegionDoesNotStartRepeatingIt()
    {
        using var playback = new PlaybackViewModel();

        playback.SetLoop(1, 2);

        Assert.IsTrue(playback.HasLoop);
        Assert.IsFalse(playback.IsLooping, "Drawing a region doesn't turn repeating it on.");
    }

    [TestMethod]
    public void DraggingTheRegionDoesNotStartRepeatingIt()
    {
        using var playback = new PlaybackViewModel();

        playback.DragLoop(0.5, 1.5);

        Assert.AreEqual(0.5, playback.Loop!.Start);
        Assert.AreEqual(1.5, playback.Loop.End);
        Assert.IsFalse(playback.IsLooping);
    }

    [TestMethod]
    public void SettingTheRegionWhileRepeatingKeepsRepeating()
    {
        using var playback = new PlaybackViewModel();
        playback.SetLoop(1, 2);
        playback.IsLooping = true;

        playback.SetLoop(3, 4);

        Assert.AreEqual(3, playback.Loop!.Start);
        Assert.IsTrue(playback.IsLooping);
    }

    [TestMethod]
    public void ClearingTheRegionStopsRepeatingIt()
    {
        using var playback = new PlaybackViewModel();
        playback.SetLoop(1, 2);
        playback.IsLooping = true;

        playback.ClearLoop();

        Assert.IsFalse(playback.HasLoop);
        Assert.IsFalse(playback.IsLooping, "There's nothing left to repeat.");
    }
}
