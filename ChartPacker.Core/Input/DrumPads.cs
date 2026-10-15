using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Input;

/// <summary>Where a drum hit lands in the game view: its lane (<see cref="ChartNote.OpenLane"/> the kick, then the pads from red), and whether it's a cymbal.</summary>
public readonly record struct DrumPad(int Lane, bool Cymbal);

/// <summary>The pro-drums colours whose tom and cymbal are bound apart, so a note of that colour needs the right one (charter-spec C19 item 6).</summary>
[Flags]
public enum CymbalSplit
{
    None = 0,
    Yellow = 1,
    Blue = 2,
    Green = 4,
}

/// <summary>
/// Where each drum action lands (charter-spec C19 item 6). A kit's pads are named by colour and kind; on 5-lane drums
/// they play as charter-spec §7.6 turns Pro into 5-lane, so a 5-lane kit's own pads (red, yellow cymbal, blue tom, orange
/// and green tom) play their own lanes, and on 4 pads the orange pad is a green cymbal, as §7.6 turns 5-lane into Pro.
/// </summary>
public static class DrumPads
{
    /// <summary>Where <paramref name="action"/> lands on drums of <paramref name="variant"/>; null for an action that isn't a drum's.</summary>
    public static DrumPad? Of(InstrumentAction action, DrumsVariant variant)
    {
        if (action == InstrumentAction.Kick)
            return new DrumPad(ChartNote.OpenLane, false);
        if (variant == DrumsVariant.FiveLane)
        {
            // Red, yellow, blue, orange, green.
            return action switch
            {
                InstrumentAction.RedPad => new DrumPad(0, false),
                InstrumentAction.YellowCymbal => new DrumPad(1, false),
                InstrumentAction.YellowTom or InstrumentAction.BlueTom => new DrumPad(2, false),
                InstrumentAction.BlueCymbal or InstrumentAction.GreenCymbal or InstrumentAction.OrangePad => new DrumPad(3, false),
                InstrumentAction.GreenTom => new DrumPad(4, false),
                _ => null,
            };
        }

        return action switch
        {
            InstrumentAction.RedPad => new DrumPad(0, false),
            InstrumentAction.YellowTom => new DrumPad(1, false),
            InstrumentAction.YellowCymbal => new DrumPad(1, true),
            InstrumentAction.BlueTom => new DrumPad(2, false),
            InstrumentAction.BlueCymbal => new DrumPad(2, true),
            InstrumentAction.GreenTom => new DrumPad(3, false),
            InstrumentAction.GreenCymbal or InstrumentAction.OrangePad => new DrumPad(3, true),
            _ => null,
        };
    }

    /// <summary>
    /// The lane record mode places <paramref name="action"/> in, as the editor numbers drums' lanes and draws them
    /// (<see cref="EditableNote.Lane"/>: 0 the kick, then the pads from red); null for an action that isn't a drum's.
    /// </summary>
    public static int? ShownLaneOf(InstrumentAction action, DrumsVariant variant) => Of(action, variant) is { } pad ? pad.Lane + 1 : null;

    /// <summary>
    /// The colours whose tom and cymbal <paramref name="bindings"/> bind apart. With one control for a colour, a note of
    /// that colour is hit by it, tom or cymbal.
    /// </summary>
    public static CymbalSplit SplitOf(IEnumerable<InputBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var actions = bindings.Select(static b => b.Action).ToHashSet();
        var split = CymbalSplit.None;
        if (actions.Contains(InstrumentAction.YellowTom) && actions.Contains(InstrumentAction.YellowCymbal))
            split |= CymbalSplit.Yellow;
        if (actions.Contains(InstrumentAction.BlueTom) && actions.Contains(InstrumentAction.BlueCymbal))
            split |= CymbalSplit.Blue;
        if (actions.Contains(InstrumentAction.GreenTom) && actions.Contains(InstrumentAction.GreenCymbal))
            split |= CymbalSplit.Green;
        return split;
    }

    /// <summary>The colour of a pro-drums lane (1 yellow, 2 blue, 3 green), or none for red and the kick.</summary>
    public static CymbalSplit ColourOf(int lane) => lane switch
    {
        1 => CymbalSplit.Yellow,
        2 => CymbalSplit.Blue,
        3 => CymbalSplit.Green,
        _ => CymbalSplit.None,
    };
}
