using ChartPacker.Core.Calibration;
using ChartPacker.Core.Settings;

namespace ChartBuilder.Tests.Calibration;

[TestClass]
public sealed class LatencyCalibrationTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartbuilder-latency-");
        _path = Path.Combine(_root.FullName, "audio-latency.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void ItStartsAtTheDevicesStoredLatency()
    {
        new AudioLatencyFile(_path).Save("Speakers", "{speakers}", 25);

        var calibration = Open("Speakers", "{speakers}");

        Assert.AreEqual(25, calibration.Value);
        Assert.AreEqual(25, calibration.Stored);
        Assert.AreEqual(LatencySource.Id, calibration.Source);
    }

    /// <summary>AC C11.3: when the output moves to another device, that device's stored latency is loaded, or the fallback.</summary>
    [TestMethod]
    public void ADeviceChangeLoadsThatDevicesLatency()
    {
        var file = new AudioLatencyFile(_path);
        file.Save("Speakers", "{speakers}", 25);
        file.Save("Headphones", "{headphones}", 190);
        var calibration = Open("Speakers", "{speakers}");
        calibration.Value = 40;

        calibration.OnDeviceChanged("Headphones", "{headphones}");
        Assert.AreEqual(190, calibration.Value);
        Assert.AreEqual("Headphones", calibration.DeviceName);

        calibration.OnDeviceChanged("New speaker", "{new}");
        Assert.AreEqual(0, calibration.Value);
        Assert.AreEqual(LatencySource.Fallback, calibration.Source);
    }

    /// <summary>AC C11.5: Cancel leaves the stored latency as it was before the tool opened.</summary>
    [TestMethod]
    public void CancelLeavesTheStoredLatencyAsItWas()
    {
        new AudioLatencyFile(_path).Save("Speakers", "{speakers}", 25);
        byte[] before = File.ReadAllBytes(_path);
        var calibration = Open("Speakers", "{speakers}");

        calibration.Value = 140;
        calibration.Cancel();

        Assert.AreEqual(25, calibration.Value);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_path));
    }

    [TestMethod]
    public void SaveStoresTheValueForTheDevice()
    {
        var calibration = Open("Speakers", "{speakers}");
        calibration.Value = -30;

        Assert.IsTrue(calibration.Save());

        Assert.AreEqual(new LatencyLookup(-30, LatencySource.Id), new AudioLatencyFile(_path).Find("Speakers", "{speakers}"));
        Assert.AreEqual(-30, calibration.Stored);
    }

    [TestMethod]
    public void ResetThenSaveStoresZero()
    {
        new AudioLatencyFile(_path).Save("Speakers", "{speakers}", 25);
        var calibration = Open("Speakers", "{speakers}");

        calibration.Reset();
        calibration.Save();

        Assert.AreEqual(0, new AudioLatencyFile(_path).Find("Speakers", "{speakers}").Milliseconds);
    }

    [TestMethod]
    public void TheValueIsKeptWithinFiveHundredMilliseconds()
    {
        var calibration = Open("Speakers", null);

        calibration.Value = 800;
        Assert.AreEqual(500, calibration.Value);
        calibration.Value = -800;
        Assert.AreEqual(-500, calibration.Value);
    }

    [TestMethod]
    public void ADeviceFoundByNameIsRememberedUnderItsId()
    {
        new AudioLatencyFile(_path).Save("Speakers", null, 25);

        Open("Speakers", "{speakers}");

        Assert.AreEqual(new LatencyLookup(25, LatencySource.Id), new AudioLatencyFile(_path).Find("Renamed", "{speakers}"));
    }

    private LatencyCalibration Open(string name, string? id) => new(new AudioLatencyFile(_path), name, id);
}
