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
