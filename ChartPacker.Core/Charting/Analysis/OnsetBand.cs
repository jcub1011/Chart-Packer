namespace ChartPacker.Core.Charting.Analysis;

/// <summary>Which part of the spectrum an onset was found in (charter-spec C14 item 1).</summary>
public enum OnsetBand : byte
{
    /// <summary>The whole spectrum: guitar, bass and the mix.</summary>
    Full,

    /// <summary>30–120 Hz, where a kick drum sits.</summary>
    Kick,

    /// <summary>150–300 Hz and 2–5 kHz, a snare's body and crack.</summary>
    Snare,

    /// <summary>6–16 kHz, cymbals and hi-hats.</summary>
    Cymbals,
}

/// <summary>An onset found in the audio: where a sound starts.</summary>
/// <param name="Seconds">When, in audio time (charter-spec §4.4.1).</param>
/// <param name="Band">Which band it was found in.</param>
/// <param name="Strength">How far it stands out from the audio around it; the sensitivity is a threshold on this.</param>
public readonly record struct DetectedOnset(double Seconds, OnsetBand Band, float Strength);
