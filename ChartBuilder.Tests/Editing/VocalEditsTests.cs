using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
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
        Assert.AreSequenceEqual(syllables, VocalEdits.SplitLyrics(line).ToArray());
    }

    [TestMethod]
    public void AMovedNotesLyricGoesWithItUnlessANoteThatStaysStartsThere()
    {
        var content = Content([new(0, 100, 60), new(0, 100, 64), new(240, 100, 62)], [new(0, "a"), new(240, "b")]);

        var alone = VocalEdits.Change(content, [new(240, 62)], n => n with { Tick = 480 }, out var moved);
        var shared = VocalEdits.Change(content, [new(0, 60)], n => n with { Tick = 120 }, out _);

        Assert.AreSequenceEqual(new[] { new VocalNotePlace(480, 62) }, moved.ToArray());
        Assert.AreSequenceEqual(new long[] { 0, 480 }, alone.Lyrics.Select(l => l.Tick).ToArray());
        Assert.AreSequenceEqual(new long[] { 0, 240 }, shared.Lyrics.Select(l => l.Tick).ToArray());
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

        Assert.AreSequenceEqual(new[] { "a", "stray" }, removed.Lyrics.Select(l => l.Text).ToArray());
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

        Assert.AreSequenceEqual(new[] { "yeah-#", "+", "shout^" }, spoken.Lyrics.Select(l => l.Text).ToArray());
        Assert.AreSequenceEqual(new[] { "yeah-", "+", "shout" }, sung.Lyrics.Select(l => l.Text).ToArray());
        Assert.IsTrue(VocalEdits.IsSpoken(spoken, 0));
        Assert.IsTrue(VocalEdits.IsSlide(spoken, 120));
    }

    [TestMethod]
    public void PhrasesThatOverlapAreFound()
    {
        Assert.IsFalse(VocalEdits.Overlap([new(0, 480), new(480, 480)]));
        Assert.IsTrue(VocalEdits.Overlap([new(480, 480), new(0, 481)]));
    }

    private static VocalTrackContent Content(VocalNoteEvent[] notes, VocalLyricEvent[] lyrics)
    {
        var chart = WithVocals(notes, lyrics);
        return VocalTrackContent.Of(chart.Passthrough[VocalTracks.IndexIn(chart, VocalTrack.Vocals)]);
    }
}
