using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The Import and Export lyrics dialog (<see cref="LyricsDialogViewModel"/>, issue 270): UltraStar is the format exported
/// unless LRC is chosen, a duet of the first two harmonies is offered when the format can have one, and a file's pitches
/// are used only with its word times.
/// </summary>
[TestClass]
public sealed class LyricsDialogTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [TestMethod]
    public void ExportingIsUltraStarToBeginWith()
    {
        var dialog = LyricsDialogViewModel.ForExport([VocalTrack.Vocals]);

        Assert.AreEqual(LyricFormat.UltraStar, dialog.Format);
        Assert.IsFalse(dialog.ShowsWordTimes);

        dialog.SelectedFormat = dialog.Formats.Single(static f => f.Format == LyricFormat.Lrc);
        Assert.IsTrue(dialog.ShowsWordTimes);
    }

    [TestMethod]
    public void TheHarmoniesAreOfferedAsADuetOnlyForUltraStar()
    {
        var dialog = LyricsDialogViewModel.ForExport([VocalTrack.Harmony1, VocalTrack.Harmony2]);
        Assert.IsTrue(dialog.Parts[^1].IsDuet);
        dialog.SelectedPart = dialog.Parts[^1];

        dialog.SelectedFormat = dialog.Formats.Single(static f => f.Format == LyricFormat.Lrc);

        Assert.IsFalse(dialog.Parts.Any(static p => p.IsDuet));
        Assert.AreEqual(VocalTrack.Harmony1, dialog.SelectedPart.Track);
        Assert.IsFalse(LyricsDialogViewModel.ForExport([VocalTrack.Vocals, VocalTrack.Harmony1]).Parts.Any(static p => p.IsDuet));
    }

    [TestMethod]
    public void ImportingADuetChoosesTheDuetToBeginWith()
    {
        var document = new LyricDocument(
        [
            new LyricLine(S(1), null, [new LyricWord("one", S(1), S(2), 60)], Singer: 1),
            new LyricLine(S(1), null, [new LyricWord("two", S(1), S(2), 64)], Singer: 2),
        ]);

        var dialog = LyricsDialogViewModel.ForImport("duet.txt", document, VocalTrack.Vocals);

        Assert.IsTrue(dialog.SelectedPart.IsDuet);
        Assert.AreEqual(VocalTrack.Harmony2, dialog.SelectedPart.Duet);
    }

    [TestMethod]
    public void PitchesAreUsedOnlyWithTheWordTimes()
    {
        var document = new LyricDocument([new LyricLine(S(1), null, [new LyricWord("one", S(1), S(2), 60)])]);
        var dialog = LyricsDialogViewModel.ForImport("song.txt", document, VocalTrack.Vocals);

        Assert.IsTrue(dialog.ShowsPitches);
        Assert.IsTrue(dialog.UsesPitches);

        dialog.UseWordTimes = false;
        Assert.IsFalse(dialog.UsesPitches);
        Assert.IsFalse(LyricsDialogViewModel.ForImport("song.lrc", document.WithoutPitches(), VocalTrack.Vocals).ShowsPitches);
    }
}
