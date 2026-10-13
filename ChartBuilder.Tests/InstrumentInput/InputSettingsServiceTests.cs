using ChartBuilder.Services;
using ChartPacker.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary><c>input-bindings.json</c> (charter-spec C19 item 2, §10, AC C19.3), always in a temp folder.</summary>
[TestClass]
public sealed class InputSettingsServiceTests
{
    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-input-");
        _path = Path.Combine(_root.FullName, "input-bindings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void AMissingFileGivesTheKeyboardsDefaults()
    {
        var settings = InputSettingsService.Load(_path).Settings;

        Assert.AreSequenceEqual(
            ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Enter", "ArrowDown", "ArrowUp"],
            settings.FiveFret.Select(b => b.Key).ToList());
        Assert.AreEqual(InstrumentAction.Kick, settings.Drums.Single(b => b.Key == "Digit0").Action);
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void BindingsAndDeviceSettingsReadBackWithTheirEnumsByName()
    {
        var kit = new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Midi, "TD-17"), "TD-17");
        var service = InputSettingsService.Load(_path);
        service.Settings.Drums = InputPresets.GeneralMidiDrums(kit);
        service.Settings.SetTuning(new DeviceTuning { Kind = InputDeviceKind.Midi, Key = "TD-17", OffsetMs = 12, MinVelocity = 18 });
        service.Save();

        string json = File.ReadAllText(_path);
        var read = InputSettingsService.Load(_path).Settings;

        StringAssert.Contains(json, "\"YellowCymbal\"");
        StringAssert.Contains(json, "\"MidiNote\"");
        Assert.HasCount(InputPresets.GeneralMidiDrumNotes.Count, read.Drums);
        Assert.AreEqual(InstrumentAction.GreenTom, read.Drums.Single(b => b.Index == 43).Action);
        var tuning = read.TuningOf(kit.Id);
        Assert.AreEqual((12, 18), (tuning.OffsetMs, tuning.MinVelocity));
    }

    [TestMethod]
    public void BindingsWithoutADeviceAndSettingsOutOfRangeAreMended()
    {
        File.WriteAllText(_path, """
            {
              "FiveFret": [ { "Action": "Green", "DeviceKind": "Controller", "DeviceKey": "", "Control": "Button" },
                            { "Action": "Red", "DeviceKind": "Keyboard", "DeviceKey": "keyboard", "Control": "Key", "Key": "Digit2" } ],
              "Devices": [ { "Kind": "Midi", "Key": "Kit", "OffsetMs": 9000, "MinVelocity": 0, "AccentVelocity": 5, "GhostVelocity": 40 } ]
            }
            """);

        var read = InputSettingsService.Load(_path).Settings;

        Assert.AreEqual(InstrumentAction.Red, read.FiveFret.Single().Action);
        var tuning = read.Devices.Single();
        Assert.AreEqual((DeviceTuning.MaxOffsetMs, 1, 41), (tuning.OffsetMs, tuning.MinVelocity, tuning.AccentVelocity));
    }

    [TestMethod]
    public void AFileWithoutAProfileKeepsTheKeyboardsDefaultsForIt()
    {
        File.WriteAllText(_path, """{ "Drums": [] }""");

        var read = InputSettingsService.Load(_path).Settings;

        Assert.IsEmpty(read.Drums);
        Assert.HasCount(InputPresets.KeyboardFiveFret().Count, read.FiveFret);
    }
}
