namespace ChartShared.Core.Theming;

/// <summary>What a themed colour is for, which decides the colour a <see cref="ThemePalette"/> gives it.</summary>
public enum ThemeRole
{
    /// <summary>A grey surface, border or input: it keeps its lightness and takes on a faint tint of the seed's hue.</summary>
    Neutral,

    /// <summary>The accent: filled buttons, checked toggles, focus rings, markers.</summary>
    Accent,

    /// <summary>The accent under the pointer: a step lighter.</summary>
    AccentHover,

    /// <summary>The accent while pressed: a step darker.</summary>
    AccentPressed,

    /// <summary>Text and icons on the accent.</summary>
    AccentForeground,

    /// <summary>Accent-coloured text on the background: pale, so it reads on dark surfaces.</summary>
    AccentText,

    /// <summary>The palest accent, almost white: what is lit up, e.g. the syllable being sung.</summary>
    AccentHighlight,

    /// <summary>
    /// The accent for marks drawn on a dark surface, e.g. the lyric lane's notes: kept light rather than darkened for
    /// text on it, so they stand out over the background and the waveform behind them.
    /// </summary>
    AccentMark,

    /// <summary>The spectrum's bass: the seed's hue turned one way.</summary>
    SpectrumLow,

    /// <summary>The spectrum's middle: the seed's hue.</summary>
    SpectrumMiddle,

    /// <summary>The spectrum's treble: the seed's hue turned the other way.</summary>
    SpectrumHigh,
}
