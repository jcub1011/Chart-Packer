using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The edits of a vocal part's notes and lyrics (charter-spec C3 item 7): a lyric belongs to the notes starting on its
/// tick, and a typed line splits into syllables.
/// </summary>
[TestClass]
public sealed class VocalEditsTests
{
    [TestMethod]
    [DataRow("Hel-lo world", new[] { "Hel-", "lo", "world" })]
    [DataRow("  one   two ", new[] { "one", "two" })]
    [DataRow("a-ha= yeah# + go--", new[] { "a-", "ha=", "yeah#", "+", "go--" })]
    [DataRow("", new string[0])]
    public void ATypedLineSplitsAtSpacesAndAfterHyphens(string line, string[] syllables)
    {
        Assert.AreSequenceEqual(syllables, [.. VocalEdits.SplitLyrics(line)]);
    }

    [TestMethod]
    public void AMovedNotesLyricGoesWithItUnlessANoteThatStaysStartsThere()
    {
        var content = Content([new(0, 100, 60), new(0, 100, 64), new(240, 100, 62)], [new(0, "a"), new(240, "b")]);

        var alone = VocalEdits.Change(content, [new(240, 62)], n => n with { Tick = 480 }, out var moved);
        var shared = VocalEdits.Change(content, [new(0, 60)], n => n with { Tick = 120 }, out _);

        Assert.AreSequenceEqual([new VocalNotePlace(480, 62)], [.. moved]);
        Assert.AreSequenceEqual([0, 480], [.. alone.Lyrics.Select(l => l.Tick)]);
        Assert.AreSequenceEqual([0, 240], [.. shared.Lyrics.Select(l => l.Tick)]);
    }

    [TestMethod]
    public void ANoteMovedOntoAnotherReplacesItAndItsLyric()
    {
        var content = Content([new(0, 100, 60), new(240, 100, 60)], [new(0, "a"), new(240, "b")]);

        var moved = VocalEdits.Change(content, [new(0, 60)], n => n with { Tick = 240 }, out _);

        Assert.AreEqual(new VocalNoteEvent(240, 100, 60), moved.Notes.Single());
        Assert.AreEqual(new VocalLyricEvent(240, "a"), moved.Lyrics.Single());
    }

    [TestMethod]
    public void RemovingANoteTakesItsLyricUnlessANoteLeftStartsThere()
    {
        var content = Content([new(0, 100, 60), new(0, 100, 64), new(240, 100, 62)], [new(0, "a"), new(240, "b"), new(480, "stray")]);

        var removed = VocalEdits.Remove(content, [new(0, 60), new(240, 62)]);

        Assert.AreSequenceEqual(["a", "stray"], [.. removed.Lyrics.Select(l => l.Text)]);
    }

    [TestMethod]
    [DataRow("hey", "hey", "")]
    [DataRow("hey#", "hey", "#")]
    [DataRow("hey-#", "hey-", "#")]
    [DataRow("hey#-", "hey-", "#")]
    [DataRow("yeah^", "yeah", "^")]
    [DataRow("#", "", "#")]
    [DataRow(" a#b ", "a#b", "")]
    [DataRow("", "", "")]
    public void TheMarksOfASpokenNoteAreSplitFromTheSyllableAfterWhichTheyWrite(string raw, string shown, string marks)
    {
        Assert.AreEqual((shown, marks), VocalEdits.SplitSpokenMarks(raw));
        Assert.AreEqual(raw.Trim() == "#" ? "#" : raw.Trim().Replace("#-", "-#", StringComparison.Ordinal), VocalEdits.WithSpokenMarks(shown, marks));
    }

    [TestMethod]
    public void ClearingALyricLeavesTheMarksOfASpokenNoteAndASlideAlone()
    {
        var content = Content([new(0, 100, 60), new(120, 100, 60), new(240, 100, 60), new(360, 100, 60)],
            [new(0, "sung-"), new(120, "talk#"), new(240, "+")]);

        var cleared = VocalEdits.ClearLyric(content, [0, 120, 240, 360]);

        Assert.AreSequenceEqual([new VocalLyricEvent(120, "#"), new VocalLyricEvent(240, "+")], [.. cleared.Lyrics]);
        Assert.IsTrue(VocalEdits.IsSpoken(cleared, 120));
    }

    [TestMethod]
    public void SettingALyricReplacesEveryLyricOnTheTickKeepingHowTheFirstIsWritten()
    {
        var content = Content([new(0, 100, 60)], [new(0, "old", VocalTrackContent.TextType), new(0, "extra")]);

        var set = VocalEdits.SetLyric(content, [0], " new- ");

        Assert.AreEqual(new VocalLyricEvent(0, "new-", VocalTrackContent.TextType), set.Lyrics.Single());
        Assert.IsEmpty(VocalEdits.SetLyric(set, [0], "  ").Lyrics);
    }

    [TestMethod]
    public void SpeakingANoteKeepsItsOtherSymbolsAndASlideIsLeftAsItIs()
    {
        var content = Content([new(0, 100, 60), new(120, 100, 62), new(240, 100, 64)], [new(0, "yeah-"), new(120, "+"), new(240, "shout^")]);

        var spoken = VocalEdits.SetSpoken(content, [0, 120], on: true);
        var sung = VocalEdits.SetSpoken(spoken, [0, 240], on: false);

        Assert.AreSequenceEqual(["yeah-#", "+", "shout^"], [.. spoken.Lyrics.Select(l => l.Text)]);
        Assert.AreSequenceEqual(["yeah-", "+", "shout"], [.. sung.Lyrics.Select(l => l.Text)]);
        Assert.IsTrue(VocalEdits.IsSpoken(spoken, 0));
        Assert.IsTrue(VocalEdits.IsSlide(spoken, 120));
    }

    [TestMethod]
    public void PhrasesThatOverlapAreFound()
    {
        Assert.IsFalse(VocalEdits.Overlap([new(0, 480), new(480, 480)]));
        Assert.IsTrue(VocalEdits.Overlap([new(480, 480), new(0, 481)]));
    }

    [TestMethod]
    public void StarPowerThatOverlapsIsFound()
    {
        Assert.IsFalse(VocalEdits.StarPowerOverlaps([new VocalStarPowerEvent(0, 480), new VocalStarPowerEvent(480, 480)]));
        Assert.IsTrue(VocalEdits.StarPowerOverlaps([new VocalStarPowerEvent(480, 480), new VocalStarPowerEvent(0, 481)]));
    }

    [TestMethod]
    public void StarPowerIsAddedRemovedAndChanged()
    {
        var content = Content([new(0, 100, 60)], [new(0, "a")]);

        var added = VocalEdits.AddStarPower(VocalEdits.AddStarPower(content, new VocalStarPowerEvent(0, 480)), new VocalStarPowerEvent(960, 480));
        var changed = VocalEdits.ChangeStarPower(added, [new VocalStarPowerEvent(960, 480)], p => p with { Length = 240 }, out var now);
        var removed = VocalEdits.RemoveStarPower(changed, [new VocalStarPowerEvent(0, 480)]);

        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 480), new VocalStarPowerEvent(960, 480)], [.. added.StarPower]);
        Assert.AreSequenceEqual([new VocalStarPowerEvent(960, 240)], [.. now]);
        Assert.AreSequenceEqual([new VocalStarPowerEvent(960, 240)], [.. removed.StarPower]);
        Assert.HasCount(1, removed.Notes, "The notes and lyrics are left as they were.");
        Assert.HasCount(1, removed.Lyrics);
    }

    [TestMethod]
    public void StarPowerIsRefusedWhereItWouldOverlapAnother()
    {
        var content = VocalEdits.AddStarPower(Content([], []), new VocalStarPowerEvent(480, 480));

        Assert.ThrowsExactly<ArgumentException>(() => VocalEdits.AddStarPower(content, new VocalStarPowerEvent(0, 720)));
        Assert.ThrowsExactly<ArgumentException>(() => VocalEdits.ChangeStarPower(VocalEdits.AddStarPower(content, new VocalStarPowerEvent(1920, 480)),
            [new VocalStarPowerEvent(1920, 480)], p => p with { Tick = 600 }, out _));
    }

    [TestMethod]
    public void RemovingStarPowerThatIsntThereChangesNothing()
    {
        var content = Content([], []);

        Assert.AreSame(content, VocalEdits.RemoveStarPower(content, [new VocalStarPowerEvent(0, 480)]));
    }

    private static VocalTrackContent Content(VocalNoteEvent[] notes, VocalLyricEvent[] lyrics)
    {
        var chart = WithVocals(notes, lyrics);
        return VocalTrackContent.Of(chart.Passthrough[VocalTracks.IndexIn(chart, VocalTrack.Vocals)]);
    }
}
