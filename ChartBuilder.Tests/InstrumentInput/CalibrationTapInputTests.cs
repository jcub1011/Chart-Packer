using ChartBuilder.Services;
using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Every device as the calibration tool's audio tap test hears it (charter-spec C11 item 1), always with a temp file.</summary>
[TestClass]
public sealed class CalibrationTapInputTests
{
    private static readonly InputDeviceInfo Guitar = new(new InputDeviceId(InputDeviceKind.Controller, "guitar"), "Guitar");
    private static readonly InputDeviceInfo Drums = new(new InputDeviceId(InputDeviceKind.Controller, "drums"), "Drums");

    private DirectoryInfo _root = null!;
    private string _path = null!;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Directory.CreateTempSubdirectory("chartpacker-calibration-taps-");
        _path = Path.Combine(_root.FullName, "input-bindings.json");
    }

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void EveryDevicesPressesCountAtTheirOwnTimesWithTheirOwnOffsets()
    {
        var source = new FakeSource();
        using var input = new InputService(InputSettingsService.Load(_path), [source]);
        input.Settings.Settings.SetTuning(new DeviceTuning { Kind = Guitar.Id.Kind, Key = Guitar.Id.Key, OffsetMs = 40 });
        input.Settings.Settings.SetTuning(new DeviceTuning { Kind = Drums.Id.Kind, Key = Drums.Id.Key, OffsetMs = -12 });
        var tapped = new List<(double Time, int Offset)>();

        using (new CalibrationTapInput(input).Listen((time, offset) => tapped.Add((time, offset))))
        {
            source.Sink!.Post(Button(Guitar.Id, 1, 1.0));
            source.Sink.Post(Button(Drums.Id, 1, 1.05));
            source.Sink.Post(Button(Guitar.Id, 0, 1.1));
            source.Sink.Post(Button(Drums.Id, 1, 1.15));
            source.Sink.Post(new RawInputEvent(Guitar.Id, new InputControl(InputControlKind.Axis, 0), 1, 1.2));
            source.Sink.Post(Button(Guitar.Id, 1, 1.3));
            input.Hub.Drain();
        }

        // Letting go is no tap, nor is a button held down pressed again, nor an axis.
        Assert.AreSequenceEqual([(1.0, 40), (1.05, -12), (1.3, 40)], tapped);
    }

    [TestMethod]
    public void ItRunsTheDevicesOnlyWhileItListens()
    {
        var source = new FakeSource();
        using var input = new InputService(InputSettingsService.Load(_path), [source]);

        var listening = new CalibrationTapInput(input).Listen((_, _) => { });
        Assert.IsTrue(input.Hub.IsRunning);
        listening.Dispose();

        Assert.IsFalse(input.Hub.IsRunning);
        Assert.AreEqual((1, 1), (source.Starts, source.Stops));
    }

    private static RawInputEvent Button(InputDeviceId device, float value, double time) =>
        new(device, new InputControl(InputControlKind.Button, 0), value, time);

    private sealed class FakeSource : IInputSource
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public IInputSink? Sink { get; private set; }

        public IReadOnlyList<InputDeviceInfo> Devices => [Guitar, Drums];

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
