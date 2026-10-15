using System.Text.Json.Serialization;

namespace ChartShared.Core.Input;

/// <summary>
/// <c>input-bindings.json</c> (charter-spec C19 item 2, §10): each profile's bindings, and each device's offset and MIDI
/// settings. Each profile is a setting of its own, so two processes changing different profiles keep both changes
/// (<see cref="Settings.JsonSettingsFile{T}"/>). Enums are saved by name.
/// </summary>
/// <remarks>A file without a profile gives the keyboard's defaults for it (<see cref="InputPresets"/>).</remarks>
public sealed class InputSettings
{
    /// <summary>The 5-fret profile's bindings.</summary>
    public List<InputBinding> FiveFret { get; set; } = InputPresets.KeyboardFiveFret();

    /// <summary>The drums profile's bindings.</summary>
    public List<InputBinding> Drums { get; set; } = InputPresets.KeyboardDrums();

    /// <summary>The devices whose settings have been changed from the defaults.</summary>
    public List<DeviceTuning> Devices { get; set; } = [];

    /// <summary>The bindings of <paramref name="profile"/>.</summary>
    public List<InputBinding> BindingsOf(InputProfileKind profile) => profile == InputProfileKind.Drums ? Drums : FiveFret;

    /// <summary>Replaces the bindings of <paramref name="profile"/>.</summary>
    public void SetBindings(InputProfileKind profile, List<InputBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (profile == InputProfileKind.Drums)
            Drums = bindings;
        else
            FiveFret = bindings;
    }

    /// <summary>The settings of <paramref name="device"/>: those saved for it, else the defaults (not added).</summary>
    public DeviceTuning TuningOf(InputDeviceId device) =>
        Devices.FirstOrDefault(d => d.Is(device)) ?? new DeviceTuning { Kind = device.Kind, Key = device.Key, Ordinal = device.Ordinal };

    /// <summary>Sets the settings of <paramref name="tuning"/>'s device, in place of any saved.</summary>
    public void SetTuning(DeviceTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        Devices = [.. Devices.Where(d => !d.Is(tuning.Device)), tuning.Sanitized()];
    }

    /// <summary>
    /// Mends what a hand-edited or older file can hold: null lists, bindings without a device, and settings out of range.
    /// </summary>
    public void Sanitize()
    {
        FiveFret = [.. (FiveFret ?? []).Where(static b => b is not null && b.IsComplete)];
        Drums = [.. (Drums ?? []).Where(static b => b is not null && b.IsComplete)];
        Devices = [.. (Devices ?? []).Where(static d => d is not null && !string.IsNullOrEmpty(d.Key)).Select(static d => d.Sanitized()).DistinctBy(static d => d.Device)];
    }
}

/// <summary>A control bound to an action (charter-spec C19 item 2).</summary>
public sealed class InputBinding
{
    [JsonConverter(typeof(JsonStringEnumConverter<InstrumentAction>))]
    public InstrumentAction Action { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<InputDeviceKind>))]
    public InputDeviceKind DeviceKind { get; set; }

    /// <summary>The device's <see cref="InputDeviceId.Key"/>.</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>The device's name when it was bound: found by this when no device has its key, as when a driver changes it.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>The device's <see cref="InputDeviceId.Ordinal"/>.</summary>
    public int Ordinal { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<InputControlKind>))]
    public InputControlKind Control { get; set; }

    /// <summary>The control's <see cref="InputControl.Index"/>; for a key, unused, as the key is saved by name.</summary>
    public int Index { get; set; }

    /// <summary>For a key, its name (Avalonia's <c>PhysicalKey</c>), which stays the same whatever the app numbers it.</summary>
    public string? Key { get; set; }

    /// <summary>The control's <see cref="InputControl.Direction"/>.</summary>
    public int Direction { get; set; }

    /// <summary>For an axis, where it rested when it was bound: it's pressed when it's moved from there.</summary>
    public float Rest { get; set; }

    /// <summary>For an axis, how far from <see cref="Rest"/>, towards <see cref="Direction"/>, it's pressed: 0 to 2.</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>The device it's on, as bound.</summary>
    [JsonIgnore]
    public InputDeviceId Device => new(DeviceKind, DeviceKey, Ordinal);

    /// <summary>Whether it names a device and a control.</summary>
    [JsonIgnore]
    public bool IsComplete => !string.IsNullOrEmpty(DeviceKey) && (Control != InputControlKind.Key || !string.IsNullOrEmpty(Key));

    /// <summary>A binding of <paramref name="action"/> to <paramref name="control"/> on <paramref name="device"/>.</summary>
    /// <param name="key">For a key, its name.</param>
    /// <param name="rest">For an axis, where it rests.</param>
    public static InputBinding Of(InstrumentAction action, InputDeviceInfo device, InputControl control, string? key = null, float rest = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new InputBinding
        {
            Action = action,
            DeviceKind = device.Id.Kind,
            DeviceKey = device.Id.Key,
            DeviceName = device.Name,
            Ordinal = device.Id.Ordinal,
            Control = control.Kind,
            Index = control.Kind == InputControlKind.Key ? 0 : control.Index,
            Key = control.Kind == InputControlKind.Key ? key : null,
            Direction = control.Direction,
            Rest = rest,
        };
    }

    /// <summary>A key on the keyboard bound to <paramref name="action"/>.</summary>
    public static InputBinding OfKey(InstrumentAction action, string key) => new()
    {
        Action = action,
        DeviceKind = InputDeviceKind.Keyboard,
        DeviceKey = InputDeviceId.Keyboard.Key,
        DeviceName = "Keyboard",
        Control = InputControlKind.Key,
        Key = key,
    };

    /// <summary>Whether <paramref name="other"/> is the same control, on the same device, whatever it plays.</summary>
    public bool IsSameControl(InputBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Device == other.Device && Control == other.Control && Direction == other.Direction
            && (Control == InputControlKind.Key ? string.Equals(Key, other.Key, StringComparison.Ordinal) : Index == other.Index);
    }

    /// <summary>A copy, playing <paramref name="action"/>.</summary>
    public InputBinding WithAction(InstrumentAction action)
    {
        var copy = (InputBinding)MemberwiseClone();
        copy.Action = action;
        return copy;
    }
}

/// <summary>A device's own settings (charter-spec C19 item 2).</summary>
public sealed class DeviceTuning
{
    /// <summary>The most a device's input offset can be, in milliseconds.</summary>
    public const int MaxOffsetMs = 250;

    /// <summary>The longest a MIDI note's retrigger time can be, in milliseconds.</summary>
    public const int MaxRetriggerMs = 100;

    [JsonConverter(typeof(JsonStringEnumConverter<InputDeviceKind>))]
    public InputDeviceKind Kind { get; set; }

    public string Key { get; set; } = "";

    public int Ordinal { get; set; }

    /// <summary>How much later than it happens the device reports an input, in milliseconds: taken off each input's time.</summary>
    public int OffsetMs { get; set; }

    /// <summary>A MIDI note softer than this is ignored, as crosstalk from another pad.</summary>
    public int MinVelocity { get; set; } = 1;

    /// <summary>A MIDI note struck again within this many milliseconds is ignored, as a double trigger.</summary>
    public int RetriggerMs { get; set; } = 12;

    /// <summary>A MIDI note at least this hard is an accent.</summary>
    public int AccentVelocity { get; set; } = 110;

    /// <summary>A MIDI note at most this hard is a ghost.</summary>
    public int GhostVelocity { get; set; } = 40;

    [JsonIgnore]
    public InputDeviceId Device => new(Kind, Key, Ordinal);

    /// <summary>Whether these are <paramref name="device"/>'s.</summary>
    public bool Is(InputDeviceId device) => Device == device;

    /// <summary>How hard a MIDI note of <paramref name="velocity"/> is.</summary>
    public HitStrength StrengthOf(int velocity) =>
        velocity >= AccentVelocity ? HitStrength.Accent : velocity <= GhostVelocity ? HitStrength.Ghost : HitStrength.Normal;

    /// <summary>A copy with every setting in range.</summary>
    public DeviceTuning Sanitized()
    {
        int min = Math.Clamp(MinVelocity, 1, 127);
        int ghost = Math.Clamp(GhostVelocity, 0, 126);
        return new DeviceTuning
        {
            Kind = Kind,
            Key = Key ?? "",
            Ordinal = Math.Max(0, Ordinal),
            OffsetMs = Math.Clamp(OffsetMs, -MaxOffsetMs, MaxOffsetMs),
            MinVelocity = min,
            RetriggerMs = Math.Clamp(RetriggerMs, 0, MaxRetriggerMs),
            GhostVelocity = ghost,
            AccentVelocity = Math.Clamp(AccentVelocity, ghost + 1, 127),
        };
    }
}
