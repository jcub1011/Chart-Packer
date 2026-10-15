using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ChartPacker.Core.Input;
using Serilog;

namespace ChartPacker.Input.Midi;

/// <summary>
/// MIDI input ports, through Windows's winmm (charter-spec C19 item 1): drum kits, pad controllers and keyboards. Each port
/// is opened only while something plays the devices, as many drivers let one app at a time hold a port; one another app
/// holds is listed as in use.
/// </summary>
/// <remarks>
/// <para>
/// winmm calls back on a thread of its own with each message and its time in milliseconds since the port started, which
/// <see cref="MidiTimestampAnchor"/> places on <see cref="InputClock"/>. The callback does nothing but post: winmm allows
/// almost nothing else there, and a port is never closed from it.
/// </para>
/// <para>winmm doesn't say when a port is plugged in, so <see cref="Rescan"/> looks again; a port unplugged closes.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinmmMidiSource : IInputSource
{
    private readonly List<Port> _ports = [];
    private IReadOnlyList<InputDeviceInfo> _infos = [];
    private IInputSink? _sink;

    public IReadOnlyList<InputDeviceInfo> Devices => _infos;

    public void Start(IInputSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_sink is not null)
            return;
        _sink = sink;
        OpenAll();
    }

    public void Stop()
    {
        CloseAll();
        _sink = null;
    }

    public void Rescan()
    {
        if (_sink is null)
            return;
        CloseAll();
        OpenAll();
    }

    public void Dispose() => Stop();

    private void OpenAll()
    {
        uint count;
        try
        {
            count = Winmm.midiInGetNumDevs();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning(ex, "winmm isn't there to read MIDI devices");
            return;
        }

        var infos = new List<InputDeviceInfo>();
        for (uint index = 0; index < count; index++)
        {
            Winmm.MidiInCaps caps;
            if (Winmm.midiInGetDevCaps(index, &caps, (uint)sizeof(Winmm.MidiInCaps)) != Winmm.NoError)
                continue;
            string name = new string(caps.Name).TrimEnd('\0');
            int ordinal = infos.Count(i => i.Id.Key == name);
            var id = new InputDeviceId(InputDeviceKind.Midi, name, ordinal);
            var port = new Port(id, _sink!);
            var problem = port.Open(index);
            if (problem == InputDeviceProblem.None)
                _ports.Add(port);
            infos.Add(new InputDeviceInfo(id, name, InputDeviceHint.Unknown, problem));
        }

        _infos = infos;
        _sink?.DevicesChanged();
    }

    private void CloseAll()
    {
        foreach (var port in _ports)
            port.Close();
        _ports.Clear();
        _infos = [];
        _sink?.DevicesChanged();
    }

    // winmm's callback for every port: its instance is the port's handle.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnMessage(nint handle, uint message, nint instance, nint param1, nint param2)
    {
        if (message != Winmm.MimData || instance == 0)
            return;
        if (GCHandle.FromIntPtr(instance).Target is Port port)
            port.Receive((uint)param1, (uint)param2);
    }

    /// <summary>One port, open.</summary>
    private sealed class Port(InputDeviceId id, IInputSink sink)
    {
        private readonly MidiTimestampAnchor _anchor = new();
        private GCHandle _self;
        private nint _handle;
        private volatile bool _closing;

        public InputDeviceProblem Open(uint index)
        {
            _self = GCHandle.Alloc(this);
            nint handle;
            nint callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&OnMessage;
            uint result = Winmm.midiInOpen(&handle, index, callback, GCHandle.ToIntPtr(_self), Winmm.CallbackFunction);
            if (result != Winmm.NoError)
            {
                _self.Free();
                Log.Information("MIDI port {Port} couldn't be opened: error {Result}", id.Key, result);
                return result == Winmm.Allocated ? InputDeviceProblem.InUse : InputDeviceProblem.CantOpen;
            }

            _handle = handle;
            _anchor.Start(InputClock.Now);
            Winmm.midiInStart(handle);
            return InputDeviceProblem.None;
        }

        // On winmm's thread.
        public void Receive(uint message, uint milliseconds)
        {
            if (_closing)
                return;
            double time = _anchor.TimeOf(milliseconds, InputClock.Now);
            if (MidiInput.Decode(message, id, time) is { } input)
                sink.Post(input);
        }

        public void Close()
        {
            if (_handle == 0)
                return;
            _closing = true;
            Winmm.midiInStop(_handle);
            Winmm.midiInReset(_handle);
            Winmm.midiInClose(_handle);
            _handle = 0;
            if (_self.IsAllocated)
                _self.Free();
        }
    }
}
