using ChartShared.Core.Calibration;
using ChartShared.Core.Settings;

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
        Assert.AreSequenceEqual(before, File.ReadAllBytes(_path));
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

    [TestMethod]
    public void ADeviceNeverCalibratedStartsAtZeroWithTheDetectedLatencyAdded()
    {
        var calibration = Open("Speakers", "{speakers}", 23);

        Assert.AreEqual(0, calibration.Value);
        Assert.AreEqual(0, calibration.Stored);
        Assert.AreEqual(23, calibration.Detected);
        Assert.AreEqual(23, calibration.Total);
        Assert.AreEqual(LatencySource.Fallback, calibration.Source);
    }

    [TestMethod]
    public void TheCalibratedLatencyIsWhatsBeyondTheDetectedOne()
    {
        new AudioLatencyFile(_path).Save("Speakers", "{speakers}", 12);

        var calibration = Open("Speakers", "{speakers}", 23);

        Assert.AreEqual(12, calibration.Value);
        Assert.AreEqual(35, calibration.Total);
        calibration.Value = -5;
        Assert.AreEqual(18, calibration.Total);
    }

    [TestMethod]
    public void SaveStoresOnlyWhatsBeyondTheDetectedLatency()
    {
        var calibration = Open("Speakers", "{speakers}", 23);
        calibration.Value = 12;

        Assert.IsTrue(calibration.Save());

        var found = new AudioLatencyFile(_path).Find("Speakers", "{speakers}", 23);
        Assert.AreEqual(new LatencyLookup(12, LatencySource.Id, 23), found);
        Assert.AreEqual(35, found.Milliseconds);
    }

    [TestMethod]
    public void ResetLeavesOnlyTheDetectedLatency()
    {
        new AudioLatencyFile(_path).Save("Speakers", "{speakers}", 60);
        var calibration = Open("Speakers", "{speakers}", 23);

        calibration.Reset();
        Assert.AreEqual(0, calibration.Value);
        Assert.AreEqual(23, calibration.Total);

        calibration.Cancel();
        Assert.AreEqual(60, calibration.Value);
    }

    [TestMethod]
    public void WithDetectingOffNothingIsAdded()
    {
        var file = new AudioLatencyFile(_path);
        file.SetDetect(false);
        file.Save("Speakers", "{speakers}", 40);

        var calibration = Open("Speakers", "{speakers}", 23);

        Assert.IsNull(calibration.Detected);
        Assert.AreEqual(40, calibration.Value);
        Assert.AreEqual(40, calibration.Total);
    }

    [TestMethod]
    public void ADeviceChangeLoadsThatDevicesDetectedLatency()
    {
        var calibration = Open("Speakers", "{speakers}", 23);

        calibration.OnDeviceChanged("Headphones", "{headphones}", 41);

        Assert.AreEqual(0, calibration.Value);
        Assert.AreEqual(41, calibration.Detected);
        Assert.AreEqual(41, calibration.Total);
    }

    private LatencyCalibration Open(string name, string? id, int? detected = null) => new(new AudioLatencyFile(_path), name, id, detected);
}
