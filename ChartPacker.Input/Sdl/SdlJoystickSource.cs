using System.Text;
using ChartPacker.Core.Input;
using SDL;
using Serilog;
using static SDL.SDL3;

namespace ChartPacker.Input.Sdl;

/// <summary>
/// Game controllers, through SDL3's joystick subsystem (charter-spec C19 item 1): every joystick SDL sees, read as raw
/// buttons, axes and hats, so a guitar or a drum controller SDL doesn't know as a gamepad still plays. Read in the
/// background too, so the window needn't have focus.
/// </summary>
/// <remarks>
/// <para>
/// SDL wants its subsystems started and stopped on the main thread, which <see cref="Start"/> and <see cref="Stop"/> are
/// called on. A thread of its own pumps SDL's events and takes the joysticks', a millisecond apart, and posts each with its
/// time: SDL's timestamp, moved onto <see cref="InputClock"/> by the two clocks as they read together on each pass.
/// </para>
/// <para>
/// On Windows, SDL handles raw input and device changes on a thread of its own (<c>SDL_HINT_JOYSTICK_THREAD</c>), as there's
/// no SDL window for them. Other events SDL queues meanwhile, such as audio device changes, have no reader, and are let go.
/// </para>
/// </remarks>
public sealed unsafe class SdlJoystickSource : IInputSource, IGamepadLayout
{
    private const SDL_InitFlags Subsystems = SDL_InitFlags.SDL_INIT_JOYSTICK | SDL_InitFlags.SDL_INIT_GAMEPAD;
    private const int EventBatch = 64;

    private readonly Lock _lock = new();
    private readonly List<Device> _devices = [];
    private IReadOnlyList<InputDeviceInfo> _infos = [];
    private IInputSink? _sink;
    private Thread? _pump;
    private volatile bool _running;
    private bool _started;

    public IReadOnlyList<InputDeviceInfo> Devices => _infos;

    public void Start(IInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_started)
            return;
        try
        {
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1"u8);
            if (OperatingSystem.IsWindows())
                SDL_SetHint(SDL_HINT_JOYSTICK_THREAD, "1"u8);
            if (!SDL_InitSubSystem(Subsystems))
            {
                Log.Warning("SDL's joysticks couldn't be started: {Error}", SDL_GetError());
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            Log.Warning(ex, "SDL isn't there to read game controllers");
            return;
        }

        _started = true;
        _sink = sink;
        using (var joysticks = SDL_GetJoysticks())
        {
            if (joysticks is not null)
            {
                for (int i = 0; i < joysticks.Count; i++)
                    Open(joysticks[i]);
            }
        }

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "SDL joysticks", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    public void Stop()
    {
        if (!_started)
            return;
        _running = false;
        _pump?.Join();
        _pump = null;
        lock (_lock)
        {
            foreach (var device in _devices)
                SDL_CloseJoystick(device.Joystick);
            _devices.Clear();
            _infos = [];
        }

        SDL_QuitSubSystem(Subsystems);
        _started = false;
        _sink = null;
    }

    /// <summary>SDL says when a controller is connected or disconnected, so there's nothing to look for.</summary>
    public void Rescan()
    {
    }

    public void Dispose() => Stop();

    /// <inheritdoc/>
    public InputControl? ControlOf(InputDeviceId device, GamepadControl control)
    {
        SDL_JoystickID id;
        lock (_lock)
        {
            if (_devices.FirstOrDefault(d => d.Info.Id == device) is not { } found)
                return null;
            id = found.Id;
        }

        if (!SDL_IsGamepad(id))
            return null;
        var gamepad = SDL_OpenGamepad(id);
        if (gamepad is null)
            return null;
        try
        {
            int count;
            var bindings = SDL_GetGamepadBindings(gamepad, &count);
            if (bindings is null)
                return null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (Matches(*bindings[i], control) && RawOf(*bindings[i]) is { } raw)
                        return raw;
                }

                return null;
            }
            finally
            {
                SDL_free(bindings);
            }
        }
        finally
        {
            SDL_CloseGamepad(gamepad);
        }
    }

    // Whether a gamepad binding's output is the standard control.
    private static bool Matches(in SDL_GamepadBinding binding, GamepadControl control)
    {
        if (control is GamepadControl.RightStickX or GamepadControl.RightStickY)
        {
            var axis = control == GamepadControl.RightStickX ? SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX : SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY;
            return binding.output_type == SDL_GamepadBindingType.SDL_GAMEPAD_BINDTYPE_AXIS && binding.output.axis.axis == axis;
        }

        var button = control switch
        {
            GamepadControl.South => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH,
            GamepadControl.East => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
            GamepadControl.West => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST,
            GamepadControl.North => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH,
            GamepadControl.LeftShoulder => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER,
            GamepadControl.RightShoulder => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER,
            GamepadControl.Back => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK,
            GamepadControl.Start => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START,
            GamepadControl.DpadUp => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP,
            _ => SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN,
        };
        return binding.output_type == SDL_GamepadBindingType.SDL_GAMEPAD_BINDTYPE_BUTTON && binding.output.button == button;
    }

    // The raw control a gamepad binding reads: a button, a hat direction, or an axis's half.
    private static InputControl? RawOf(in SDL_GamepadBinding binding) => binding.input_type switch
    {
        SDL_GamepadBindingType.SDL_GAMEPAD_BINDTYPE_BUTTON => new InputControl(InputControlKind.Button, binding.input.button),
        SDL_GamepadBindingType.SDL_GAMEPAD_BINDTYPE_HAT => new InputControl(InputControlKind.Hat, binding.input.hat.hat, binding.input.hat.hat_mask),
        SDL_GamepadBindingType.SDL_GAMEPAD_BINDTYPE_AXIS => new InputControl(
            InputControlKind.Axis, binding.input.axis.axis, binding.input.axis.axis_max >= binding.input.axis.axis_min ? 1 : -1),
        _ => null,
    };

    // Opens a joystick, lists it, and posts where its axes rest, so a binding can tell a moved axis from one at rest.
    private void Open(SDL_JoystickID id)
    {
        lock (_lock)
        {
            if (_devices.Any(d => d.Id == id))
                return;
        }

        var joystick = SDL_OpenJoystick(id);
        if (joystick is null)
        {
            Log.Warning("A game controller couldn't be opened: {Error}", SDL_GetError());
            return;
        }

        string key = GuidOf(SDL_GetJoystickGUID(joystick));
        string name = SDL_GetJoystickName(joystick) ?? key;
        var hint = SDL_GetJoystickType(joystick) switch
        {
            SDL_JoystickType.SDL_JOYSTICK_TYPE_GUITAR => InputDeviceHint.Guitar,
            SDL_JoystickType.SDL_JOYSTICK_TYPE_DRUM_KIT => InputDeviceHint.DrumKit,
            SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD => InputDeviceHint.Gamepad,
            _ => InputDeviceHint.Unknown,
        };

        Device device;
        lock (_lock)
        {
            int ordinal = _devices.Count(d => d.Info.Id.Key == key);
            device = new Device(id, joystick, new InputDeviceInfo(new InputDeviceId(InputDeviceKind.Controller, key, ordinal), name, hint));
            _devices.Add(device);
            _infos = [.. _devices.Select(static d => d.Info)];
        }

        double now = InputClock.Now;
        int axes = SDL_GetNumJoystickAxes(joystick);
        for (int axis = 0; axis < axes; axis++)
        {
            short rest;
            if (!SDL_GetJoystickAxisInitialState(joystick, axis, &rest))
                rest = SDL_GetJoystickAxis(joystick, axis);
            _sink?.Post(new RawInputEvent(device.Info.Id, new InputControl(InputControlKind.Axis, axis), AxisValue(rest), now));
        }

        _sink?.DevicesChanged();
    }

    private void Close(SDL_JoystickID id)
    {
        lock (_lock)
        {
            int index = _devices.FindIndex(d => d.Id == id);
            if (index < 0)
                return;
            SDL_CloseJoystick(_devices[index].Joystick);
            _devices.RemoveAt(index);
            _infos = [.. _devices.Select(static d => d.Info)];
        }

        _sink?.DevicesChanged();
    }

    private void Pump()
    {
        try
        {
            PumpEvents();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the game controllers failed");
        }
    }

    private void PumpEvents()
    {
        var events = new SDL_Event[EventBatch];
        while (_running)
        {
            SDL_PumpEvents();
            ulong sdlNow = SDL_GetTicksNS();
            double now = InputClock.Now;
            int count;
            do
            {
                count = SDL_PeepEvents(events, SDL_EventAction.SDL_GETEVENT, SDL_EventType.SDL_EVENT_JOYSTICK_FIRST, SDL_EventType.SDL_EVENT_JOYSTICK_LAST);
                for (int i = 0; i < count; i++)
                    Take(events[i], sdlNow, now);
            }
            while (count == EventBatch);

            // Nothing else reads SDL's queue: what isn't a joystick's is let go, so it doesn't fill.
            SDL_FlushEvents(SDL_EventType.SDL_EVENT_FIRST, SDL_EventType.SDL_EVENT_JOYSTICK_FIRST - 1);
            SDL_FlushEvents(SDL_EventType.SDL_EVENT_JOYSTICK_LAST + 1, SDL_EventType.SDL_EVENT_LAST);
            SDL_DelayPrecise(1_000_000);
        }
    }

    private void Take(in SDL_Event e, ulong sdlNow, double now)
    {
        switch (e.Type)
        {
            case SDL_EventType.SDL_EVENT_JOYSTICK_ADDED:
                Open(e.jdevice.which);
                return;
            case SDL_EventType.SDL_EVENT_JOYSTICK_REMOVED:
                Close(e.jdevice.which);
                return;
            case SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_DOWN or SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_UP:
                Post(e.jbutton.which, new InputControl(InputControlKind.Button, e.jbutton.button), e.jbutton.down ? 1 : 0, e.jbutton.timestamp, sdlNow, now);
                return;
            case SDL_EventType.SDL_EVENT_JOYSTICK_AXIS_MOTION:
                Post(e.jaxis.which, new InputControl(InputControlKind.Axis, e.jaxis.axis), AxisValue(e.jaxis.value), e.jaxis.timestamp, sdlNow, now);
                return;
            case SDL_EventType.SDL_EVENT_JOYSTICK_HAT_MOTION:
                Post(e.jhat.which, new InputControl(InputControlKind.Hat, e.jhat.hat), e.jhat.value, e.jhat.timestamp, sdlNow, now);
                return;
        }
    }

    // An event, at its SDL timestamp moved onto the input clock, never later than now.
    private void Post(SDL_JoystickID id, InputControl control, float value, ulong timestamp, ulong sdlNow, double now)
    {
        InputDeviceId? device = null;
        lock (_lock)
        {
            foreach (var each in _devices)
            {
                if (each.Id == id)
                {
                    device = each.Info.Id;
                    break;
                }
            }
        }

        if (device is not { } found || _sink is not { } sink)
            return;
        double age = timestamp <= sdlNow ? (sdlNow - timestamp) / 1e9 : 0;
        sink.Post(new RawInputEvent(found, control, value, now - age));
    }

    private static float AxisValue(short value) => Math.Clamp(value / 32767f, -1f, 1f);

    private static string GuidOf(SDL_GUID guid)
    {
        byte* text = stackalloc byte[33];
        SDL_GUIDToString(guid, text, 33);
        return Encoding.ASCII.GetString(text, 32);
    }

    private sealed class Device(SDL_JoystickID id, SDL_Joystick* joystick, InputDeviceInfo info)
    {
        public SDL_JoystickID Id { get; } = id;

        public SDL_Joystick* Joystick { get; } = joystick;

        public InputDeviceInfo Info { get; } = info;
    }
}
