using ChartShared.Core.Input;

namespace ChartBuilder.Tests.InstrumentInput;

/// <summary>Backends run while leased, their inputs drained in the order they happened (charter-spec C19 item 1).</summary>
[TestClass]
public sealed class InputHubTests
{
    [TestMethod]
    public void BackendsRunWhileAnythingHoldsALease()
    {
        var source = new FakeSource();
        using var hub = new InputHub([source], () => { });

        var first = hub.Acquire();
        var second = hub.Acquire();
        first.Dispose();
        first.Dispose();
        Assert.AreEqual((1, 0), (source.Starts, source.Stops));

        second.Dispose();
        Assert.AreEqual((1, 1), (source.Starts, source.Stops));
        Assert.IsFalse(hub.IsRunning);
    }

    [TestMethod]
    public void InputsAreDrainedInTheOrderTheyHappenedAndWakeIsCalledOnceUntilThen()
    {
        var source = new FakeSource();
        int wakes = 0;
        using var hub = new InputHub([source], () => wakes++);
        using var lease = hub.Acquire();
        hub.Drain();
        wakes = 0;
        var drained = new List<double>();
        hub.Received += batch => drained.AddRange(batch.Select(e => e.Time));

        source.Sink!.Post(Input(2.0));
        source.Sink.Post(Input(1.0));
        source.Sink.Post(Input(1.5));
        Assert.AreEqual(1, wakes);
        hub.Drain();

        Assert.AreSequenceEqual([1.0, 1.5, 2.0], drained);
        Assert.AreEqual(1f, hub.LastValue(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1)));
    }

    [TestMethod]
    public void AnInputObservedIsLastReadButNotPlayed()
    {
        var source = new FakeSource();
        using var hub = new InputHub([source], () => { });
        int received = 0;
        hub.Received += _ => received++;

        hub.Observe(Input(1.0));
        Assert.IsNull(hub.LastValue(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1)), "Observed while the backends weren't running.");

        using var lease = hub.Acquire();
        hub.Observe(Input(2.0));
        hub.Drain();

        Assert.AreEqual(0, received);
        Assert.AreEqual(1f, hub.LastValue(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1)));
        Assert.AreEqual(2.0, hub.LastActivity(InputDeviceId.Keyboard));
    }

    [TestMethod]
    public void ReleasingKeysLetsGoOfTheKeyboardsKeysOnly()
    {
        var source = new FakeSource();
        using var hub = new InputHub([source], () => { });
        using var lease = hub.Acquire();
        var pad = new InputDeviceId(InputDeviceKind.Controller, "pad", 0);
        var button = new InputControl(InputControlKind.Button, 0);
        hub.Observe(Input(1.0));
        hub.Observe(new RawInputEvent(pad, button, 1, 1.0));

        hub.ReleaseKeys();

        Assert.AreEqual(0f, hub.LastValue(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1)));
        Assert.AreEqual(1f, hub.LastValue(pad, button));
        Assert.AreEqual(1.0, hub.LastActivity(InputDeviceId.Keyboard), "Letting go isn't something the keyboard did.");
    }

    private static RawInputEvent Input(double time) => new(InputDeviceId.Keyboard, new InputControl(InputControlKind.Key, 1), 1, time);

    private sealed class FakeSource : IInputSource
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public IInputSink? Sink { get; private set; }

        public IReadOnlyList<InputDeviceInfo> Devices => [];

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
