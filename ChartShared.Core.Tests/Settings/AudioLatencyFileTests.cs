using System.Text.Json.Nodes;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Settings;

[TestClass]
public sealed class AudioLatencyFileTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-latency-");
        _path = Path.Combine(_root.FullName, "audio-latency.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileGivesTheFallbackOfZero()
    {
        Assert.AreEqual(new LatencyLookup(0, LatencySource.Fallback), Open().Find("Speakers", "{speakers}"));
    }

    [TestMethod]
    public void ACorruptFileGivesTheFallbackOfZero()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.AreEqual(new LatencyLookup(0, LatencySource.Fallback), Open().Find("Speakers", "{speakers}"));
    }

    [TestMethod]
    public void TheIdComesBeforeTheNameAndTheNameBeforeTheFallback()
    {
        File.WriteAllText(_path, """{ "byId": { "{a}": 10 }, "byName": { "A": 20, "B": 30 }, "fallback": 40 }""");
        var file = Open();

        Assert.AreEqual(new LatencyLookup(10, LatencySource.Id), file.Find("A", "{a}"));
        Assert.AreEqual(new LatencyLookup(30, LatencySource.Name), file.Find("B", "{b}"));
        Assert.AreEqual(new LatencyLookup(20, LatencySource.Name), file.Find("A", null));
        Assert.AreEqual(new LatencyLookup(40, LatencySource.Fallback), file.Find("C", "{c}"));
    }

    [TestMethod]
    public void ASavedLatencyIsFoundByIdAndByName()
    {
        Assert.IsTrue(Open().Save("Headphones", "{headphones}", 120));

        Assert.AreEqual(new LatencyLookup(120, LatencySource.Id), Open().Find("Renamed", "{headphones}"));
        Assert.AreEqual(new LatencyLookup(120, LatencySource.Name), Open().Find("Headphones", "{other}"));
    }

    [TestMethod]
    public void ADeviceWithoutAnIdIsSavedByName()
    {
        Open().Save("Speakers", null, -15);

        Assert.AreEqual(new LatencyLookup(-15, LatencySource.Name), Open().Find("Speakers", null));
        Assert.IsNull(Read()["byId"]);
    }

    [TestMethod]
    public void LatenciesAreKeptWithinFiveHundredMilliseconds()
    {
        Open().Save("Bluetooth", null, 900);
        Assert.AreEqual(500, (int)Read()["byName"]!["Bluetooth"]!);

        File.WriteAllText(_path, """{ "byName": { "Edited": 900, "Odd": -2000.4 } }""");
        Assert.AreEqual(500, Open().Find("Edited", null).Milliseconds);
        Assert.AreEqual(-500, Open().Find("Odd", null).Milliseconds);
    }

    [TestMethod]
    public void AValueFoundByNameIsRememberedUnderTheId()
    {
        Open().Save("Speakers", null, 35);
        var file = Open();

        Assert.AreEqual(LatencySource.Name, file.Find("Speakers", "{speakers}").Source);
        file.RememberUnderId("Speakers", "{speakers}");

        Assert.AreEqual(new LatencyLookup(35, LatencySource.Id), Open().Find("Speakers (renamed)", "{speakers}"));
    }

    [TestMethod]
    public void RememberingDoesNotReplaceAValueUnderTheId()
    {
        File.WriteAllText(_path, """{ "byId": { "{a}": 10 }, "byName": { "A": 20 } }""");

        Open().RememberUnderId("A", "{a}");

        Assert.AreEqual(10, Open().Find("A", "{a}").Milliseconds);
    }

    [TestMethod]
    public void TwoWritersKeepEachOthersDevices()
    {
        var first = Open();
        var second = Open();

        first.Save("Speakers", "{speakers}", 20);
        second.Save("Headphones", "{headphones}", 180);

        Assert.AreEqual(20, Open().Find("Speakers", "{speakers}").Milliseconds);
        Assert.AreEqual(180, Open().Find("Headphones", "{headphones}").Milliseconds);
    }

    [TestMethod]
    public void UnknownKeysAreKept()
    {
        File.WriteAllText(_path, """{ "fromLater": { "x": 1 }, "fallback": 5 }""");

        Open().Save("Speakers", null, 20);

        var json = Read();
        Assert.AreEqual(1, (int)json["fromLater"]!["x"]!);
        Assert.AreEqual(5, (int)json["fallback"]!);
    }

    [TestMethod]
    public void NoTempFileIsLeftBehind()
    {
        Open().Save("Speakers", "{speakers}", 20);

        // The lock file is deleted as it's let go on Windows only.
        Assert.AreSequenceEqual(new[] { "audio-latency.json" }, [.. _root.GetFiles().Select(f => f.Name).Where(n => !n.EndsWith(".lock", StringComparison.Ordinal))]);
    }

    [TestMethod]
    public void ASaveWaitsForAnotherProcessesUpdateAndFailsIfItNeverEnds()
    {
        using (new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.IsFalse(Open().Save("Speakers", "{speakers}", 20));
        Assert.IsFalse(File.Exists(_path));

        Assert.IsTrue(Open().Save("Speakers", "{speakers}", 20));
        Assert.AreEqual(new LatencyLookup(20, LatencySource.Id), Open().Find("Speakers", "{speakers}"));
    }

    [TestMethod]
    public void ADeviceWithNeitherNameNorIdIsNotSaved()
    {
        Assert.IsFalse(Open().Save("", null, 20));
        Assert.IsFalse(File.Exists(_path));
    }

    private AudioLatencyFile Open() => new(_path);

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_path))!;
}
