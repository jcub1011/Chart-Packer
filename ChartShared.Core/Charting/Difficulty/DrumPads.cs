using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// How a drums note's pads are laid out as <see cref="DifficultyNote.Lanes"/> bits (docs/difficulty-model.md §14.1), and
/// where on the kit each is.
/// </summary>
/// <remarks>
/// <para>The kick is <see cref="Kick"/>, the top bit. The pads are from bit 0, red, as the game view's lanes are: 4-lane
/// red, yellow, blue and green, and 5-lane red, yellow, blue, orange and green.</para>
/// <para>For Pro drums, bits 1 to 3 are the yellow, blue and green toms, and bits 4 to 6 the cymbals of those colours: a
/// row of their own above the toms. Read as 4-lane drums, a cymbal is the tom of its colour.</para>
/// <para>The Pro layout is only for drums the games read as Pro (<see cref="GameView.IsProDrums"/>): it has no bit for
/// 5-lane's fifth pad, so 5-lane drums are always read as <see cref="DifficultyInstrument.Drums"/>.</para>
/// </remarks>
public static class DrumPads
{
    /// <summary>The kick's bit.</summary>
    public const byte Kick = 0x80;

    /// <summary>The pads' bits: every one but the kick.</summary>
    public const byte Pads = 0x7F;

    // The first Pro cymbal's bit, yellow's.
    private const int FirstCymbal = 4;

    /// <summary>
    /// <paramref name="note"/>'s bit, read as <paramref name="instrument"/> reads it; 0 for an Expert+ kick, which Expert
    /// doesn't play.
    /// </summary>
    public static byte BitOf(ChartNote note, DifficultyInstrument instrument)
    {
        if ((note.Flags & NoteFlags.DoubleKick) != 0)
            return 0;
        if (note.Lane == ChartNote.OpenLane)
            return Kick;
        if (note.Lane is < 0 or > 4)
            return 0;
        bool cymbal = instrument == DifficultyInstrument.ProDrums && note.Lane is >= 1 and <= 3 && (note.Flags & NoteFlags.Cymbal) != 0;
        return (byte)(1 << (cymbal ? note.Lane + FirstCymbal - 1 : note.Lane));
    }

    /// <summary>Whether <paramref name="bit"/>, one pad's, is a Pro cymbal.</summary>
    public static bool IsCymbal(int bit, DifficultyInstrument instrument) =>
        instrument == DifficultyInstrument.ProDrums && bit is >= FirstCymbal and < 7;

    /// <summary>
    /// Where the pad <paramref name="bit"/> is across the kit, in pads' widths from red; a cymbal is above the tom of its
    /// colour.
    /// </summary>
    public static double PositionOf(int bit, DifficultyInstrument instrument) =>
        IsCymbal(bit, instrument) ? bit - FirstCymbal + 1 : bit;
}
