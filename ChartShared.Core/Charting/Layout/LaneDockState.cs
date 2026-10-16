namespace ChartShared.Core.Charting.Layout;

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

    /// <summary>How many seconds a lane shows beyond its strike line; null for the editor's default.</summary>
    public double? LookAhead { get; set; }

    /// <summary>How a lane shows its audio; null for the defaults.</summary>
    public LaneAudioState? Audio { get; set; }
}

/// <summary>
/// A lane's <see cref="LaneAudioOptions"/> as plain data. A value this version doesn't know, or a missing one, is that
/// option's default.
/// </summary>
public sealed class LaneAudioState
{
    public string? Source { get; set; }

    public string? Backdrop { get; set; }

    public string? Placement { get; set; }

    public string? Channels { get; set; }

    public double? GainDb { get; set; }

    public string? Band { get; set; }

    public double? FloorDb { get; set; }

    public double? CeilingDb { get; set; }

    public bool? ShowOnsets { get; set; }

    public double? OnsetSensitivity { get; set; }

    public static LaneAudioState Of(LaneAudioOptions options) => new()
    {
        Source = options.Source,
        Backdrop = options.Backdrop?.ToString(),
        Placement = options.Placement.ToString(),
        Channels = options.Channels.ToString(),
        GainDb = options.GainDb,
        Band = options.Band.ToString(),
        FloorDb = options.FloorDb,
        CeilingDb = options.CeilingDb,
        ShowOnsets = options.ShowOnsets,
        OnsetSensitivity = options.OnsetSensitivity,
    };

    public LaneAudioOptions ToOptions()
    {
        var defaults = LaneAudioOptions.Default;
        return new LaneAudioOptions(
            string.IsNullOrWhiteSpace(Source) ? null : Source,
            Enum.TryParse<LaneBackdrop>(Backdrop, out var backdrop) && Enum.IsDefined(backdrop) ? backdrop : null,
            Parse(Placement, defaults.Placement),
            Parse(Channels, defaults.Channels),
            GainDb ?? defaults.GainDb,
            Parse(Band, defaults.Band),
            FloorDb ?? defaults.FloorDb,
            CeilingDb ?? defaults.CeilingDb,
            ShowOnsets ?? defaults.ShowOnsets,
            OnsetSensitivity ?? defaults.OnsetSensitivity).Clamped();
    }

    private static T Parse<T>(string? value, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
