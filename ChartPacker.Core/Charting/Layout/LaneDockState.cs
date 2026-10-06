namespace ChartPacker.Core.Charting.Layout;

/// <summary>A <see cref="LaneDockLayout"/> as plain data, for saving to settings.</summary>
/// <remarks>
/// Its names are strings rather than enums, so a name a later version no longer knows only makes
/// <see cref="LaneDockLayout.FromState"/> fall back, instead of failing the whole settings file.
/// </remarks>
public sealed class LaneDockState
{
    /// <summary>The whole dock, or null when every lane is closed.</summary>
    public LaneDockNodeState? Root { get; set; }
}

/// <summary>A split, when <see cref="Split"/> is set, or else a lane.</summary>
public sealed class LaneDockNodeState
{
    /// <summary>A split's <see cref="DockOrientation"/>.</summary>
    public string? Split { get; set; }

    /// <summary>A split's children, in order.</summary>
    public List<LaneDockNodeState>? Children { get; set; }

    /// <summary>A split's children's weights, one per child.</summary>
    public List<double>? Weights { get; set; }

    /// <summary>A highway lane's <see cref="Charts.ChartInstrument"/>.</summary>
    public string? Instrument { get; set; }

    /// <summary>A highway lane's <see cref="Charts.ChartDifficulty"/>.</summary>
    public string? Difficulty { get; set; }

    /// <summary>A vocal lane's <see cref="VocalTrack"/>.</summary>
    public string? Vocal { get; set; }
}
