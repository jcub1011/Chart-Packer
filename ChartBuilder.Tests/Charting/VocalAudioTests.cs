using ChartBuilder.ViewModels;
using ChartShared.Audio.Analysis;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Scanning;
using ChartShared.UI.Controls;

namespace ChartBuilder.Tests.Charting;

/// <summary>The audio a vocal lane's piano roll is drawn over: which stem it takes, and how the spectrogram lines up with the keys.</summary>
[TestClass]
public sealed class VocalAudioTests
{
    private static readonly SongTrack Song = new("song.ogg", "song", null);
    private static readonly SongTrack Lead = new("vocals.ogg", "vocals", null);
    private static readonly SongTrack Harmony = new("vocals_1.ogg", "vocals_1", null);

    [TestMethod]
    public void ALeadIsDrawnOverItsOwnStem()
    {
        var source = AudioSource.Resolve(LaneAudioOptions.Default, LaneStems.For(VocalTrack.Vocals), [Song, Lead, Harmony]);

        Assert.IsNotNull(source);
        Assert.IsFalse(source.IsMix);
        Assert.AreSequenceEqual([Lead], [.. source.Tracks]);
    }

    [TestMethod]
    public void AHarmonyIsDrawnOverItsOwnStemBeforeTheLeads()
    {
        var source = AudioSource.Resolve(LaneAudioOptions.Default, LaneStems.For(VocalTrack.Harmony1), [Song, Lead, Harmony]);

        Assert.IsNotNull(source);
        Assert.AreSequenceEqual([Harmony], [.. source.Tracks]);
    }

    [TestMethod]
    public void AVocalPartWithoutAStemIsDrawnOverTheMix()
    {
        var source = AudioSource.Resolve(LaneAudioOptions.Default, LaneStems.For(VocalTrack.Vocals), [Song]);

        Assert.IsNotNull(source);
        Assert.IsTrue(source.IsMix);
    }

    [TestMethod]
    public void AChosenStemOrTheMixIsUsedInPlaceOfTheVocalPartsOwn()
    {
        var mix = AudioSource.Resolve(LaneAudioOptions.Default with { Source = LaneAudioOptions.MixSource }, LaneStems.For(VocalTrack.Vocals), [Song, Lead]);
        var chosen = AudioSource.Resolve(LaneAudioOptions.Default with { Source = "song" }, LaneStems.For(VocalTrack.Vocals), [Song, Lead]);

        Assert.IsTrue(mix!.IsMix);
        Assert.AreSequenceEqual([Song], [.. chosen!.Tracks]);
    }

    [TestMethod]
    public void ThePickerOffersTheVocalStemFirstAsTheAutomaticChoice()
    {
        var choices = AudioSource.ChoicesFor(LaneStems.For(VocalTrack.Harmony1), [Song, Lead, Harmony]);

        Assert.IsNull(choices[0].Source);
        StringAssert.Contains(choices[0].Label, "vocals_1");
        Assert.Contains(c => c.Source == LaneAudioOptions.MixSource, choices);
    }

    [TestMethod]
    public void TheSpectrogramPutsEachKeysRowAcrossItsNotesFrequency()
    {
        const double firstRowTop = 100, rowSize = 20;
        var frame = SpectrogramPainter.PitchFrame.Of(84, firstRowTop, rowSize);

        foreach (int pitch in new[] { 84, 72, 60, 36 })
        {
            double centre = firstRowTop + ((84 - pitch + 0.5) * rowSize);
            double drawnAt = frame.Offset + (frame.Scale * SpectrogramRows.PictureYOfPitch(pitch));
            Assert.AreEqual(centre, drawnAt, 1e-6, $"Pitch {pitch}'s frequency is drawn across its key's middle.");
        }
    }

    [TestMethod]
    public void AKeyIsAsTallAsTheRowsOfASemitone()
    {
        var frame = SpectrogramPainter.PitchFrame.Of(84, 0, 30);

        Assert.AreEqual(30, frame.Scale * SpectrogramRows.RowsPerSemitone, 1e-9);
    }
}
