using ChartShared.UI.ViewModels;
using ChartBuilder.Tests.Charting.Saving;
using static ChartBuilder.Tests.Charting.Saving.SaveFolders;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// What Add track and Replace do with the file chosen, in Chart Packer's inspector and Chart Builder's Chart settings:
/// check it, name it after its stem, and work out what it replaces.
/// </summary>
[TestClass]
public sealed class TrackStagingTests
{
    private SaveFolders _folders = null!;

    [TestInitialize]
    public void CreateFolders() => _folders = new SaveFolders();

    [TestCleanup]
    public void DeleteFolders() => _folders.Dispose();

    [TestMethod]
    public async Task AnAudioFileCanBeATrack()
    {
        string wav = _folders.Loose("Lead Take 3.WAV", Wav(100));

        var check = await TrackStaging.CheckAsync(wav);

        Assert.IsTrue(check.IsUsable, check.Error);
        Assert.AreEqual(new FileInfo(wav).Length, check.Length);
    }

    [TestMethod]
    public async Task AFileThatIsntAudioIsRefused()
    {
        var notAudio = await TrackStaging.CheckAsync(_folders.Loose("cover.png", [1, 2, 3]));
        var notWhatItSays = await TrackStaging.CheckAsync(_folders.Loose("broken.ogg", [1, 2, 3]));

        Assert.IsFalse(notAudio.IsUsable);
        Assert.Contains("cover.png", notAudio.Error!);
        Assert.IsFalse(notWhatItSays.IsUsable);
        Assert.Contains("broken.ogg", notWhatItSays.Error!);
    }

    [TestMethod]
    public void ATrackIsStoredAsItsStemAndReplacesTheStemInAnyFormat()
    {
        string stored = TrackStaging.StoredNameFor("guitar", @"C:\Audio\Lead.OGG");

        Assert.AreEqual("guitar.ogg", stored);
        Assert.AreSequenceEqual(
            ["Guitar.ogg", "guitar.mp3"],
            TrackStaging.Replaced(["notes.mid", "Guitar.ogg", "guitar.mp3", "guitar.ogg", "bass.ogg"], "guitar", stored).ToList());
    }

    [TestMethod]
    public void TheAddTrackMenuOffersToReplaceTheStemsTheSongHas()
    {
        var options = TrackStaging.OptionsFor(["guitar"], _ => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

        Assert.HasCount(ChartShared.Core.Scanning.AudioStems.Stems.Count, options);
        Assert.AreNotEqual("guitar", options.Single(o => o.Stem == "guitar").Label);
        Assert.AreEqual("bass", options.Single(o => o.Stem == "bass").Label);
    }
}
