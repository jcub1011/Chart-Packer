using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The session's status, which the Editor's message bar shows: every status said is posted, even one the same as the
/// last, so a refusal repeated after something else was said shows again.
/// </summary>
[TestClass]
public sealed class EditorSessionStatusTests
{
    [TestMethod]
    public void TheSameStatusSaidTwiceIsPostedTwiceButChangesTheStateOnce()
    {
        var session = Of(Guitar());
        var posted = new List<string>();
        int stateChanges = 0;
        session.StatusPosted += (_, status) => posted.Add(status);
        session.StateChanged += (_, _) => stateChanges++;

        session.Status = "Refused";
        session.Status = "Refused";

        Assert.AreSequenceEqual(["Refused", "Refused"], posted);
        Assert.AreEqual(1, stateChanges);
    }

    [TestMethod]
    public void ClearingTheStatusPostsNothing()
    {
        var session = Of(Guitar());
        session.Status = "Refused";
        var posted = new List<string>();
        session.StatusPosted += (_, status) => posted.Add(status);

        session.Status = null;

        Assert.IsEmpty(posted);
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    public void ARefusedEditIsPosted()
    {
        // A 2x kick is on Expert only (C6 item 2), which the session says why.
        var hard = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard);
        var session = Of(Drums(), focused: hard);
        var posted = new List<string>();
        session.StatusPosted += (_, status) => posted.Add(status);

        session.Tools.ToggleDoubleKickAtCursor();

        Assert.HasCount(1, posted);
        Assert.AreEqual(session.Status, posted[0]);
    }
}
