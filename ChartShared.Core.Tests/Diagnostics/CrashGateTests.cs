using ChartShared.Core.Diagnostics;
using ChartShared.Core.Tests.Audio;

namespace ChartShared.Core.Tests.Diagnostics;

[TestClass]
public sealed class CrashGateTests
{
    [TestMethod]
    public void TheFirstErrorShowsTheDialog()
    {
        var gate = new CrashGate(new ManualClock());

        Assert.AreEqual(CrashResponse.Show, gate.Next());
        Assert.AreEqual(0, gate.Extra);
    }

    [TestMethod]
    public void ErrorsWhileItIsOpenAreCounted()
    {
        var gate = new CrashGate(new ManualClock());
        gate.Next();

        Assert.AreEqual(CrashResponse.Absorb, gate.Next());
        Assert.AreEqual(CrashResponse.Absorb, gate.Next());
        Assert.AreEqual(2, gate.Extra);
    }

    [TestMethod]
    public void ErrorsLongAfterItOpenedWithoutBeingDrawnEndTheApp()
    {
        var clock = new ManualClock();
        var gate = new CrashGate(clock);
        gate.Next();
        clock.Advance(CrashGate.DrawGrace - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(CrashResponse.Absorb, gate.Next());

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(CrashResponse.LetCrash, gate.Next());
    }

    [TestMethod]
    public void OnceDrawnEveryErrorIsCounted()
    {
        var clock = new ManualClock();
        var gate = new CrashGate(clock);
        gate.Next();
        gate.Drawn();

        // A timer that throws on every tick, long after the dialog came up.
        for (int i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(16));
            Assert.AreEqual(CrashResponse.Absorb, gate.Next());
        }

        Assert.AreEqual(1000, gate.Extra);
    }

    [TestMethod]
    public void ClosingShowsTheDialogAgainForTheNextError()
    {
        // Whether the user kept working or chose to quit: the windows may still ask about unsaved changes.
        var gate = new CrashGate(new ManualClock());
        gate.Next();
        gate.Next();
        gate.Closed();

        Assert.AreEqual(CrashResponse.Show, gate.Next());
        Assert.AreEqual(0, gate.Extra);
    }

    [TestMethod]
    public void DrawingTheNextDialogIsWaitedForAgain()
    {
        var clock = new ManualClock();
        var gate = new CrashGate(clock);
        gate.Next();
        gate.Drawn();
        gate.Closed();
        gate.Next();

        clock.Advance(CrashGate.DrawGrace);

        Assert.AreEqual(CrashResponse.LetCrash, gate.Next());
    }

    [TestMethod]
    public void AnErrorAsTheAppShutsDownEndsIt()
    {
        var gate = new CrashGate(new ManualClock());
        gate.Next();
        gate.Closed();
        gate.ShuttingDown();

        Assert.AreEqual(CrashResponse.LetCrash, gate.Next());
    }

    [TestMethod]
    public void AnErrorAfterTheDialogFailedEndsTheApp()
    {
        var gate = new CrashGate(new ManualClock());
        gate.Next();
        gate.Failed();

        Assert.AreEqual(CrashResponse.LetCrash, gate.Next());
    }

    [TestMethod]
    public void AFailedQuitShowsTheDialogAgain()
    {
        var gate = new CrashGate(new ManualClock());
        gate.Next();
        gate.Closed();

        Assert.IsTrue(gate.QuitFailed());
        Assert.AreEqual(CrashResponse.Show, gate.Next());
    }

    [TestMethod]
    public void ASecondFailedQuitEndsTheApp()
    {
        var gate = new CrashGate(new ManualClock());
        gate.Next();
        gate.Closed();
        gate.QuitFailed();
        gate.Next();
        gate.Closed();

        Assert.IsFalse(gate.QuitFailed());
        Assert.AreEqual(CrashResponse.LetCrash, gate.Next());
    }
}
