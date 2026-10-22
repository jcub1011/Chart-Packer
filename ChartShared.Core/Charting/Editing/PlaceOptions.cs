namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// What the Place tool makes of a note it puts down, as the toolbar picks it. The defaults change nothing already in the
/// chart: a note joining a chord leaves the chord's marking, and a pad a tom marker covers stays a tom. A choice other than
/// the default is made of whatever the note lands in.
/// </summary>
/// <param name="ChordType">
/// On fret parts, what the chord placed in is made: force HOPO, force strum or tap; <see cref="ChordFlags.None"/> leaves it
/// as it is.
/// </param>
/// <param name="Open">On fret parts, whether a click places an open note in any column, as Alt does; Alt then places a fret.</param>
/// <param name="DragSustains">On fret parts, whether a drag draws out a sustain, as Ctrl does; Ctrl then lays a run.</param>
/// <param name="Tom">On Pro drums, whether a yellow, blue or green pad placed is made a tom, on every difficulty.</param>
/// <param name="Dynamics">On drums, whether a pad placed is an accent or a ghost; kicks are left plain.</param>
/// <param name="DoubleKick">On Expert drums, whether a kick placed is a 2× kick.</param>
public sealed record PlaceOptions(
    ChordFlags ChordType = ChordFlags.None,
    bool Open = false,
    bool DragSustains = false,
    bool Tom = false,
    NoteDynamics Dynamics = NoteDynamics.None,
    bool DoubleKick = false)
{
    /// <summary>Nothing picked: a plain note, as the Place tool always placed one.</summary>
    public static PlaceOptions Default { get; } = new();
}
