using System.Collections.Concurrent;
using Serilog;

namespace ChartShared.Core.Input;

/// <summary>
/// Every backend's input in one place (charter-spec C19): backends post from their own threads, and the thread that owns
/// the hub, the UI thread, drains it, in the order the inputs happened. Backends run only while the hub is leased
/// (<see cref="Acquire"/>): by playtest, record mode or the Input devices window.
/// </summary>
/// <remarks>
/// <para>An input's time is its own event's, so how long it waits here changes nothing but how soon it's drawn.</para>
/// <para>Everything but <see cref="Post"/> and <see cref="IInputSink.DevicesChanged"/> belongs to the owning thread.</para>
/// </remarks>
public sealed class InputHub : IInputSink, IDisposable
{
    private readonly IReadOnlyList<IInputSource> _sources;
    private readonly Action _wake;
    private readonly ConcurrentQueue<RawInputEvent> _queue = new();
    private readonly List<RawInputEvent> _batch = [];
    private readonly Dictionary<(InputDeviceId, InputControl), float> _last = [];
    private readonly Dictionary<InputDeviceId, double> _activity = [];
    private int _wakePending;
    private int _devicesChanged;
    private int _leases;
    private bool _disposed;

    /// <param name="sources">The backends.</param>
    /// <param name="wake">
    /// Called from any thread when inputs are waiting, once until the next <see cref="Drain"/>: it should have the owning
    /// thread call <see cref="Drain"/> soon.
    /// </param>
    public InputHub(IReadOnlyList<IInputSource> sources, Action wake)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(wake);
        _sources = sources;
        _wake = wake;
    }

    /// <summary>Raised by <see cref="Drain"/> with the inputs since the last, in the order they happened.</summary>
    public event Action<IReadOnlyList<RawInputEvent>>? Received;

    /// <summary>Raised by <see cref="Drain"/> when a backend's devices changed.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Whether the backends are running: whether anything holds a lease.</summary>
    public bool IsRunning => _leases > 0;

    /// <summary>Every backend's devices connected now.</summary>
    public IReadOnlyList<InputDeviceInfo> Devices => [.. _sources.SelectMany(static s => s.Devices)];

    /// <summary>Starts the backends if they aren't running, until the lease is disposed and no other is held.</summary>
    public IDisposable Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_leases++ == 0)
        {
            foreach (var source in _sources)
            {
                try
                {
                    source.Start(this);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "An input backend ({Source}) couldn't be started", source.GetType().Name);
                }
            }

            ((IInputSink)this).DevicesChanged();
        }

        return new Lease(this);
    }

    /// <summary>Has every backend look for devices connected or disconnected.</summary>
    public void Rescan()
    {
        if (!IsRunning)
            return;
        foreach (var source in _sources)
            source.Rescan();
        ((IInputSink)this).DevicesChanged();
    }

    /// <inheritdoc/>
    public void Post(in RawInputEvent input)
    {
        _queue.Enqueue(input);
        Wake();
    }

    void IInputSink.DevicesChanged()
    {
        Volatile.Write(ref _devicesChanged, 1);
        Wake();
    }

    /// <summary>
    /// Hands the inputs waiting to <see cref="Received"/>, in the order they happened, and says when devices changed. On
    /// the owning thread.
    /// </summary>
    public void Drain()
    {
        Volatile.Write(ref _wakePending, 0);
        if (Interlocked.Exchange(ref _devicesChanged, 0) == 1)
            DevicesChanged?.Invoke(this, EventArgs.Empty);

        _batch.Clear();
        while (_queue.TryDequeue(out var input))
            _batch.Add(input);
        if (_batch.Count == 0)
            return;

        // Each backend posts in order, so the batch is nearly sorted already: an insertion sort, which keeps inputs at
        // one time in the order they came.
        for (int i = 1; i < _batch.Count; i++)
        {
            var input = _batch[i];
            int j = i - 1;
            while (j >= 0 && _batch[j].Time > input.Time)
            {
                _batch[j + 1] = _batch[j];
                j--;
            }

            _batch[j + 1] = input;
        }

        foreach (var input in _batch)
        {
            _last[(input.Device, input.Control)] = input.Value;
            _activity[input.Device] = input.Time;
        }

        Received?.Invoke(_batch);
    }

    /// <summary>
    /// Takes <paramref name="input"/> as its control's last value and its device's last activity, without handing it to
    /// <see cref="Received"/>: a control seen but not played, as the Input devices window's keys are, so a playtest running
    /// meanwhile isn't played by them. On the owning thread; nothing while the backends aren't running.
    /// </summary>
    public void Observe(in RawInputEvent input)
    {
        if (!IsRunning)
            return;
        _last[(input.Device, input.Control)] = input.Value;
        _activity[input.Device] = input.Time;
    }

    /// <summary>
    /// Lets go of the keyboard's keys as last read, as a window loses the keyboard and won't hear them let go. Nothing is
    /// played; it's only what <see cref="LastValue"/> and <see cref="Snapshot"/> give.
    /// </summary>
    public void ReleaseKeys()
    {
        foreach (var key in _last.Keys.Where(static k => k.Item1 == InputDeviceId.Keyboard).ToList())
            _last[key] = 0;
    }

    /// <summary>The value <paramref name="control"/> last had, as drained; null when it hasn't moved since the backends started.</summary>
    public float? LastValue(InputDeviceId device, InputControl control) =>
        _last.TryGetValue((device, control.Whole), out float value) ? value : null;

    /// <summary>Every control's last value, as drained, copied: what's held and where axes rest now (<see cref="BindingDetector"/>).</summary>
    public IReadOnlyDictionary<(InputDeviceId, InputControl), float> Snapshot() => new Dictionary<(InputDeviceId, InputControl), float>(_last);

    /// <summary>When <paramref name="device"/> last did anything, on <see cref="InputClock"/>; null for not since the backends started.</summary>
    public double? LastActivity(InputDeviceId device) => _activity.TryGetValue(device, out double time) ? time : null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_leases > 0)
            StopSources();
        _leases = 0;
        foreach (var source in _sources)
            source.Dispose();
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
            _wake();
    }

    private void Release()
    {
        if (_disposed || _leases == 0)
            return;
        if (--_leases == 0)
            StopSources();
    }

    private void StopSources()
    {
        foreach (var source in _sources)
        {
            try
            {
                source.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "An input backend ({Source}) couldn't be stopped", source.GetType().Name);
            }
        }

        // What the devices last read is stale once they're closed: a control held then may be let go before they open again.
        _last.Clear();
        _activity.Clear();
        while (_queue.TryDequeue(out _))
        {
        }
    }

    private sealed class Lease(InputHub hub) : IDisposable
    {
        private InputHub? _hub = hub;

        public void Dispose() => Interlocked.Exchange(ref _hub, null)?.Release();
    }
}
