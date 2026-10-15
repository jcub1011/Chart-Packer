using ChartShared.Core.Charts;

namespace ChartShared.Core.Input;

/// <summary>
/// What a control plays (charter-spec C19 item 2). Saved by name, so actions added later (6-fret's) can't change what a
/// saved binding plays.
/// </summary>
public enum InstrumentAction
{
    // 5-fret.
    Green,
    Red,
    Yellow,
    Blue,
    Orange,
    StrumUp,
    StrumDown,
    Whammy,
    StarPower,

    // Drums, by the pad on a kit: a colour's tom and cymbal can be one control or two.
    Kick,
    RedPad,
    YellowTom,
    YellowCymbal,
    BlueTom,
    BlueCymbal,
    GreenTom,
    GreenCymbal,

    /// <summary>A 5-lane kit's fifth pad, a cymbal between blue and green.</summary>
    OrangePad,
}

/// <summary>An instrument family with a profile of bindings of its own (charter-spec C19 item 2).</summary>
public enum InputProfileKind
{
    FiveFret,
    Drums,
}

/// <summary>How hard a drum was hit, from a MIDI device's velocity and its thresholds (<see cref="DeviceTuning"/>).</summary>
public enum HitStrength
{
    Normal,
    Accent,
    Ghost,
}

/// <summary>Facts about each <see cref="InstrumentAction"/>.</summary>
public static class InstrumentActions
{
    /// <summary>The 5-fret profile's actions, in the order the Input devices window lists them.</summary>
    public static IReadOnlyList<InstrumentAction> FiveFret { get; } =
    [
        InstrumentAction.Green, InstrumentAction.Red, InstrumentAction.Yellow, InstrumentAction.Blue, InstrumentAction.Orange,
        InstrumentAction.StrumUp, InstrumentAction.StrumDown, InstrumentAction.Whammy, InstrumentAction.StarPower,
    ];

    /// <summary>The drums profile's actions, in the order the Input devices window lists them.</summary>
    public static IReadOnlyList<InstrumentAction> Drums { get; } =
    [
        InstrumentAction.Kick, InstrumentAction.RedPad, InstrumentAction.YellowTom, InstrumentAction.YellowCymbal,
        InstrumentAction.BlueTom, InstrumentAction.BlueCymbal, InstrumentAction.GreenTom, InstrumentAction.GreenCymbal,
        InstrumentAction.OrangePad,
    ];

    /// <summary>The actions of <paramref name="profile"/>.</summary>
    public static IReadOnlyList<InstrumentAction> Of(InputProfileKind profile) =>
        profile == InputProfileKind.Drums ? Drums : FiveFret;

    /// <summary>The profile an instrument of <paramref name="kind"/> plays with; null for one that isn't played in v1 (6-fret).</summary>
    public static InputProfileKind? ProfileOf(ChartInstrumentKind kind) => kind switch
    {
        ChartInstrumentKind.FiveFret => InputProfileKind.FiveFret,
        ChartInstrumentKind.Drums => InputProfileKind.Drums,
        _ => null,
    };

    /// <summary>The fret <paramref name="action"/> is, 0 green to 4 orange; null for another action.</summary>
    public static int? FretOf(InstrumentAction action) =>
        action is >= InstrumentAction.Green and <= InstrumentAction.Orange ? action - InstrumentAction.Green : null;

    /// <summary>Whether <paramref name="action"/> strums.</summary>
    public static bool IsStrum(InstrumentAction action) => action is InstrumentAction.StrumUp or InstrumentAction.StrumDown;

    /// <summary>
    /// Whether <paramref name="action"/> is a drum hit: every press of every control bound to it is a hit, where a fret
    /// is pressed once however many of its controls are held.
    /// </summary>
    public static bool IsDrumHit(InstrumentAction action) => action is >= InstrumentAction.Kick and <= InstrumentAction.OrangePad;
}
