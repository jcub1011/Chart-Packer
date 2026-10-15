using ChartPacker.Core.Input;

namespace ChartPacker.Core.Charting.Playtest;

/// <summary>What the judge says of an input or a note (charter-spec C19 items 4 to 6).</summary>
public enum JudgeKind
{
    /// <summary>A note hit.</summary>
    Hit,

    /// <summary>A note not hit before its window closed, or skipped by hitting a later one.</summary>
    Miss,

    /// <summary>A strum with nothing to hit, or the wrong frets. Breaks the combo.</summary>
    Overstrum,

    /// <summary>A pad hit with no note to hit. Breaks the combo unless <see cref="DrumRules.OverhitBreaksCombo"/> is off.</summary>
    Overhit,

    /// <summary>A sustain let go too early. Counted, but the combo goes on.</summary>
    SustainDrop,

    /// <summary>A sustain held to its end.</summary>
    SustainDone,

    /// <summary>A pressed fret wrong for the next HOPO or tap, which must now be strummed.</summary>
    Ghost,

    /// <summary>An accent hit softly or a ghost hit hard (<see cref="DrumRules.JudgeDynamics"/>). The note is still hit.</summary>
    WrongDynamics,
}

/// <summary>One thing the judge said.</summary>
/// <param name="Note">The note's index in <see cref="PlaytestChart.Notes"/>; −1 for an overstrum or overhit, which has none.</param>
/// <param name="Song">When, in audio seconds.</param>
/// <param name="OffsetMs">For a hit, how far from the note in real milliseconds, early negative; for a dropped sustain, how early; 0 otherwise.</param>
/// <param name="Lane">The game view lane: an overhit's pad, else the note's first lane; −2 for an overstrum.</param>
/// <param name="Combo">The combo after it.</param>
public readonly record struct JudgeEvent(JudgeKind Kind, int Note, double Song, double OffsetMs, int Lane, int Combo);

/// <summary>
/// A playtest's rules for one instrument (charter-spec C19 items 5 and 6), fed timed actions in the order they happened.
/// Deterministic: the same actions over the same chart give the same judgements, so a performance can be judged again.
/// </summary>
public interface IPlaytestEngine
{
    PlaytestChart Chart { get; }

    /// <summary>How many notes in a row have been hit without a miss, an overstrum or an overhit.</summary>
    int Combo { get; }

    /// <summary>
    /// Starts again at <paramref name="song"/>, as after a seek or the loop's jump back: notes whose window has closed by
    /// then are passed over unjudged, and the combo starts at 0.
    /// </summary>
    /// <param name="isDown">Which actions are held now.</param>
    void Reset(double song, double speed, Func<InstrumentAction, bool> isDown);

    /// <summary>Judges <paramref name="action"/>, first judging everything up to its time, adding what's said to <paramref name="output"/>.</summary>
    void Input(in TimedAction action, List<JudgeEvent> output);

    /// <summary>Judges what time alone settles up to <paramref name="song"/>: misses, strums that waited too long, sustains ended.</summary>
    void Advance(double song, double speed, List<JudgeEvent> output);
}
