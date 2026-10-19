using ChartBuilder.Services;
using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>A device as the calibration tool's input-device test measures it (charter-spec C11 item 2), always with a temp file.</summary>
[TestClass]
public sealed class DeviceCalibrationInputTests
{
    private static readonly InputDeviceInfo Guitar = new(new InputDeviceId(InputDeviceKind.Controller, "guitar"), "Guitar");
    private static readonly InputDeviceId Other = new(InputDeviceKind.Controller, "other");

    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-calibration-input-");
        _path = Path.Combine(_root.FullName, "input-bindings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void ItHandsOnTheDevicesPressesAtTheirOwnTimesWithNoOffsetTakenOff()
    {
        var source = new FakeSource();
        using var input = new InputService(InputSettingsService.Load(_path), [source]);
        input.Settings.Settings.SetTuning(new DeviceTuning { Kind = Guitar.Id.Kind, Key = Guitar.Id.Key, OffsetMs = 40 });
        var calibration = new DeviceCalibrationInput(input, Guitar);
        var pressed = new List<double>();

        using (calibration.Listen(pressed.Add))
        {
            source.Sink!.Post(Button(Guitar.Id, 1, 1.0));
            source.Sink.Post(Button(Guitar.Id, 0, 1.1));
            source.Sink.Post(Button(Other, 1, 1.2));
            source.Sink.Post(new RawInputEvent(Guitar.Id, new InputControl(InputControlKind.Hat, 0), HatDirections.Down, 1.3));
            input.Hub.Drain();
        }

        Assert.AreSequenceEqual([1.0, 1.3], pressed);
        Assert.AreEqual(40, calibration.OffsetMs);
    }

    [TestMethod]
    public void ItRunsTheDevicesOnlyWhileItListens()
    {
        var source = new FakeSource();
        using var input = new InputService(InputSettingsService.Load(_path), [source]);
        var calibration = new DeviceCalibrationInput(input, Guitar);

        var listening = calibration.Listen(_ => { });
        Assert.IsTrue(input.Hub.IsRunning);
        listening.Dispose();

        Assert.IsFalse(input.Hub.IsRunning);
        Assert.AreEqual((1, 1), (source.Starts, source.Stops));
    }

    [TestMethod]
    public void SavingStoresTheOffsetKeepingTheDevicesOtherSettings()
    {
        using var input = new InputService(InputSettingsService.Load(_path), [new FakeSource()]);
        input.Settings.Settings.SetTuning(new DeviceTuning { Kind = Guitar.Id.Kind, Key = Guitar.Id.Key, MinVelocity = 30 });
        bool rebound = false;
        input.BindingsChanged += (_, _) => rebound = true;

        Assert.IsTrue(new DeviceCalibrationInput(input, Guitar).Save(-18));

        var saved = InputSettingsService.Load(_path).Settings.TuningOf(Guitar.Id);
        Assert.AreEqual((-18, 30), (saved.OffsetMs, saved.MinVelocity));
        Assert.IsTrue(rebound);
    }

    private static RawInputEvent Button(InputDeviceId device, float value, double time) =>
        new(device, new InputControl(InputControlKind.Button, 0), value, time);

    private sealed class FakeSource : IInputSource
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public IInputSink? Sink { get; private set; }

        public IReadOnlyList<InputDeviceInfo> Devices => [Guitar];

        public void Start(IInputSink sink)
        {
            Starts++;
            Sink = sink;
        }

        public void Stop() => Stops++;

        public void Rescan()
        {
        }

        public void Dispose()
        {
        }
    }
}
