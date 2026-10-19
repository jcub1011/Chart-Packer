using System;
using System.Collections.Generic;
using Avalonia.Input;
using ChartBuilder.Instruments;
using ChartBuilder.Services;
using ChartShared.Core.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The instrument devices played into the editor (charter-spec C19 item 3), for playtest and record mode: the backends
    /// kept running, a profile's bindings, and each action placed in the song by when it happened, through the playback
    /// clock as it was then, sampled each frame. What's played goes to <see cref="IActionSink"/>.
    /// </summary>
    internal sealed class InputPlayer : IDisposable
    {
        private readonly InputService _input;
        private readonly PlaybackViewModel _playback;
        private readonly IActionSink _sink;
        private readonly IDisposable _lease;
        private readonly List<ActionEvent> _actions = [];
        private InputProfileKind? _profile;
        private InputRouter? _router;
        private bool _disposed;

        public InputPlayer(InputService input, PlaybackViewModel playback, IActionSink sink)
        {
            _input = input;
            _playback = playback;
            _sink = sink;
            _lease = input.Hub.Acquire();
            input.Hub.Received += OnReceived;
            input.Hub.DevicesChanged += OnRebind;
            input.BindingsChanged += OnRebind;
            Sample();
        }

        /// <summary>The playback clock's samples, which place what's played in the song.</summary>
        public SongClockMap Clock { get; } = new();

        /// <summary>
        /// The profile whose bindings play: the instrument of the lane played. Changing it lets go of what the last one held.
        /// Null plays nothing.
        /// </summary>
        public InputProfileKind? Profile
        {
            get => _profile;
            set
            {
                if (value == _profile)
                    return;
                ReleaseAll();
                _profile = value;
                _router = value is { } profile ? _input.RouterFor(profile) : null;
            }
        }

        /// <summary>
        /// Whether the keyboard plays too. Record mode takes keys as it always has, by the number keys, so only playtest
        /// plays them through the bindings.
        /// </summary>
        public bool PlaysKeys { get; set; }

        /// <summary>Whether <paramref name="action"/> is held.</summary>
        public bool IsDown(InstrumentAction action) => _router?.IsDown(action) == true;

        /// <summary>Samples the playback clock: each frame, and when playing starts or stops.</summary>
        public void Sample() =>
            Clock.Sample(InputClock.Now, _playback.Clock().TotalSeconds, _playback.SpeedPercent / 100.0, _playback.IsPlaying);

        /// <summary>Plays <paramref name="key"/>, pressed or let go, if the keyboard plays and the profile binds it.</summary>
        /// <returns>Whether it's bound.</returns>
        public bool Key(PhysicalKey key, bool down)
        {
            if (!PlaysKeys || _router is not { } router || !router.IsBound(InputDeviceId.Keyboard, KeyboardInputSource.ControlOf(key)))
                return false;
            _input.PostKey(key, down);
            return true;
        }

        /// <summary>
        /// Lets go of the keys held, as when the window loses focus or the song stops: in the hub too, so the Input devices
        /// window doesn't show them held.
        /// </summary>
        public void ReleaseKeys()
        {
            _input.Hub.ReleaseKeys();
            _router?.ReleaseKeys(InputClock.Now, _actions);
            Send();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _input.Hub.Received -= OnReceived;
            _input.Hub.DevicesChanged -= OnRebind;
            _input.BindingsChanged -= OnRebind;
            ReleaseAll();
            _lease.Dispose();
        }

        private void ReleaseAll()
        {
            _router?.ReleaseAll(InputClock.Now, _actions);
            Send();
        }

        private void OnReceived(IReadOnlyList<RawInputEvent> inputs)
        {
            if (_router is not { } router)
                return;
            foreach (var input in inputs)
            {
                if (PlaysKeys || input.Device.Kind != InputDeviceKind.Keyboard)
                    router.Handle(input, _actions);
            }

            Send();
        }

        // The bindings or the devices changed: what's held is let go, and the bindings found again.
        private void OnRebind(object? sender, EventArgs e)
        {
            ReleaseAll();
            if (_profile is { } profile)
                _router = _input.RouterFor(profile);
        }

        // Each action, placed in the song; those while it isn't playing are dropped.
        private void Send()
        {
            foreach (var action in _actions)
            {
                if (Clock.Map(action) is { } timed)
                    _sink.Handle(timed);
            }

            _actions.Clear();
        }
    }
}
