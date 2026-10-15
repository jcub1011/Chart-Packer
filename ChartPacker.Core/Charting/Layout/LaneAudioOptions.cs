namespace ChartPacker.Core.Charting.Layout;

/// <summary>What a lane draws its audio as (charter-spec C12, C13).</summary>
public enum LaneBackdrop
{
    Waveform,
    Spectrogram,
}

/// <summary>Where a lane draws its audio (charter-spec C12 item 3).</summary>
public enum LaneAudioPlacement
{
    /// <summary>Faintly behind the lanes.</summary>
    Behind,

    /// <summary>At full strength in a column of its own beside them.</summary>
    Beside,
}

/// <summary>How a lane's waveform shows the channels (charter-spec C12 item 4).</summary>
public enum LaneChannels
{
    /// <summary>The channels combined.</summary>
    Mono,

    /// <summary>Left and right, side by side.</summary>
    Stereo,
}

/// <summary>A spectrogram band preset (charter-spec C13 item 4): a range emphasised, the rest dimmed.</summary>
public enum SpectrogramBand
{
    Full,

    /// <summary>30–120 Hz.</summary>
    Kick,

    /// <summary>150–300 Hz and 2–5 kHz.</summary>
    Snare,

    /// <summary>6–16 kHz.</summary>
    Cymbals,

    /// <summary>40–400 Hz.</summary>
    Bass,
}

/// <summary>
/// How one lane shows its audio: which audio, as a waveform or spectrogram, where, and the onsets beside it. Kept with
/// the lane layout.
/// </summary>
/// <param name="Source">
/// The audio: <see langword="null"/> for the stem that matches the lane's part (<c>LaneStems</c>), <see cref="MixSource"/>
/// for the whole mix, or a stem's name.
/// </param>
/// <param name="Backdrop">Waveform or spectrogram; <see langword="null"/> follows the setting.</param>
/// <param name="GainDb">How much louder than full scale the waveform is drawn, for quiet stems.</param>
/// <param name="FloorDb">The spectrogram's contrast floor, in dB of a full-scale sine: anything quieter is drawn black.</param>
/// <param name="CeilingDb">The spectrogram's contrast ceiling, in dB of a full-scale sine: anything louder is drawn at full colour.</param>
/// <param name="OnsetSensitivity">From 0 (fewest onsets) to 1 (most).</param>
public sealed record LaneAudioOptions(
    string? Source = null,
    LaneBackdrop? Backdrop = null,
    LaneAudioPlacement Placement = LaneAudioPlacement.Behind,
    LaneChannels Channels = LaneChannels.Mono,
    double GainDb = 0,
    SpectrogramBand Band = SpectrogramBand.Full,
    double FloorDb = -80,
    double CeilingDb = 0,
    bool ShowOnsets = true,
    double OnsetSensitivity = 0.5)
{
    /// <summary><see cref="Source"/>'s value for the whole mix.</summary>
    public const string MixSource = "*mix";

    public const double MinGainDb = -12;
    public const double MaxGainDb = 24;
    public const double MinFloorDb = -120;
    public const double MinCeilingDb = -60;

    public static LaneAudioOptions Default { get; } = new();

    /// <summary>These options with every number within its range, and the ceiling above the floor.</summary>
    public LaneAudioOptions Clamped()
    {
        double ceiling = Clamp(CeilingDb, MinCeilingDb, 0, 0);
        double floor = Clamp(FloorDb, MinFloorDb, ceiling - 6, -80);
        return this with
        {
            GainDb = Clamp(GainDb, MinGainDb, MaxGainDb, 0),
            CeilingDb = ceiling,
            FloorDb = Math.Min(floor, ceiling - 6),
            OnsetSensitivity = Clamp(OnsetSensitivity, 0, 1, 0.5),
        };
    }

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
