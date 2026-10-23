using ChartBuilder.Services;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Settings;
using ChartShared.UI.Services;

namespace ChartBuilder.Tests.Editing;

/// <summary>The waveform detail setting (issue #150), as saved in Chart Builder's settings, always in a temp folder.</summary>
[TestClass]
public sealed class WaveformFidelitySettingTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-waveform-");
        _path = Path.Combine(_root.FullName, "builder-settings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void WaveformsDefaultToHigh()
    {
        Assert.AreEqual(WaveformFidelity.High, new BuilderSettings().WaveformFidelity);
    }

    [TestMethod]
    public void AMissingFileReadsHigh()
    {
        Assert.AreEqual(WaveformFidelity.High, BuilderSettingsService.Load(_path).Settings.WaveformFidelity);
    }

    [TestMethod]
    public void AnUnknownFidelityFallsBackToHigh()
    {
        File.WriteAllText(_path, """{ "WaveformFidelity": 99 }""");
        Assert.AreEqual(WaveformFidelity.High, BuilderSettingsService.Load(_path).Settings.WaveformFidelity);
    }

    [TestMethod]
    public void AFidelityReadsBack()
    {
        File.WriteAllText(_path, """{ "WaveformFidelity": "Medium" }""");
        Assert.AreEqual(WaveformFidelity.Medium, BuilderSettingsService.Load(_path).Settings.WaveformFidelity);

        var service = BuilderSettingsService.Load(_path);
        service.Settings.WaveformFidelity = WaveformFidelity.Ultra;
        service.Save();

        Assert.AreEqual(WaveformFidelity.Ultra, BuilderSettingsService.Load(_path).Settings.WaveformFidelity);
    }

    [TestMethod]
    public void TheCatalogShowsWaveformDetailBesideChartPacker()
    {
        var settings = BuilderSettingsService.Load(_path);
        var latency = new AudioLatencyService(new AudioLatencyFile(Path.Combine(_root.FullName, "latency.json")));
        var catalog = BuilderSettingsCatalog.Create(settings, latency, () => { }, () => { }, () => { }, () => { });

        var fidelity = catalog.Definitions.Single(d => d.Id == "playback.waveformFidelity") as ChoiceSetting<WaveformFidelity>;
        Assert.IsNotNull(fidelity, "Waveform detail sits in Playback like Chart Packer's playback.waveformFidelity.");
        Assert.AreEqual("playback.visualizers", fidelity.Category.Id);
        Assert.AreEqual(WaveformFidelity.High, fidelity.DefaultValue);
        CollectionAssert.AreEqual(
            new[] { WaveformFidelity.Low, WaveformFidelity.Medium, WaveformFidelity.High, WaveformFidelity.Ultra },
            fidelity.Choices.Select(c => c.Value).ToArray());

        Assert.AreEqual("playback.audio", catalog.Definitions.Single(d => d.Id == "playback.masterVolume").Category.Id);
        Assert.AreEqual("playback.visualizers", catalog.Definitions.Single(d => d.Id == "playback.laneBackdrop").Category.Id);
        Assert.AreEqual("playback.visualizers", catalog.Definitions.Single(d => d.Id == "playback.spectrogramBudget").Category.Id);
        Assert.IsFalse(catalog.Definitions.Any(d => d.Id == "audio.masterVolume" || d.Id == "charter.laneBackdrop" || d.Id == "charter.spectrogramBudget"));
    }
}
