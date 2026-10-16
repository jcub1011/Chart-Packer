using ChartShared.Core.Settings;

namespace ChartShared.Core.Calibration;

/// <summary>
/// The latency being calibrated for the output device, as the calibration tool edits it (charter-spec C11):
/// <see cref="Save"/> stores it, <see cref="Cancel"/> goes back to what was stored, <see cref="Reset"/> sets 0.
/// </summary>
public sealed class LatencyCalibration
{
    private readonly AudioLatencyFile _file;
    private int _value;

    /// <summary>Loads the stored latency of the device with <paramref name="deviceName"/> and <paramref name="deviceId"/>.</summary>
    public LatencyCalibration(AudioLatencyFile file, string deviceName, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        Load(deviceName, deviceId);
    }

    public string DeviceName { get; private set; } = "";

    public string? DeviceId { get; private set; }

    /// <summary>The latency stored for the device when it was loaded or last saved, in milliseconds.</summary>
    public int Stored { get; private set; }

    /// <summary>Where <see cref="Stored"/> came from; <see cref="LatencySource.Fallback"/> means the device was never calibrated.</summary>
    public LatencySource Source { get; private set; }

    /// <summary>The latency being tried, in milliseconds, within ±<see cref="AudioLatencyFile.MaxMilliseconds"/>.</summary>
    public int Value
    {
        get => _value;
        set => _value = Math.Clamp(value, -AudioLatencyFile.MaxMilliseconds, AudioLatencyFile.MaxMilliseconds);
    }

    /// <summary>The output moved to another device: that device's stored latency is loaded in place of this one's (AC C11.3).</summary>
    public void OnDeviceChanged(string deviceName, string? deviceId) => Load(deviceName, deviceId);

    /// <summary>Stores <see cref="Value"/> for the device. Gives false when it couldn't be written.</summary>
    public bool Save()
    {
        if (!_file.Save(DeviceName, DeviceId, Value))
            return false;

        Stored = Value;
        Source = string.IsNullOrEmpty(DeviceId) ? LatencySource.Name : LatencySource.Id;
        return true;
    }

    /// <summary>Goes back to the stored latency, writing nothing (AC C11.5).</summary>
    public void Cancel() => Value = Stored;

    /// <summary>Sets the latency to 0, to be saved or cancelled.</summary>
    public void Reset() => Value = 0;

    private void Load(string deviceName, string? deviceId)
    {
        DeviceName = deviceName ?? "";
        DeviceId = deviceId;
        var found = _file.FindAndRemember(DeviceName, DeviceId);
        Stored = found.Milliseconds;
        Source = found.Source;
        Value = found.Milliseconds;
    }
}
