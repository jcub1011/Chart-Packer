namespace ChartShared.Core.Validation;

/// <summary>The groups of the chart lint rule catalogue (charter-spec §6), by the range their IDs are in.</summary>
public enum ChrCategory
{
    /// <summary>CHR001 to CHR019: the tempo map and the grid.</summary>
    Timing,

    /// <summary>CHR020 to CHR039: 5-fret and GHL.</summary>
    Frets,

    /// <summary>CHR040 to CHR059: drums.</summary>
    Drums,

    /// <summary>CHR060 to CHR079: phrases and events.</summary>
    Phrases,

    /// <summary>CHR080 to CHR085, and CHR087 to CHR089: the song as a whole.</summary>
    Song,

    /// <summary>CHR086: what the chart file held that couldn't be loaded.</summary>
    Load,

    /// <summary>CHR090 to CHR109: the vocal parts.</summary>
    Vocals,
}

/// <summary>
/// Rule IDs from the chart lint rule catalogue (charter-spec §6), for <see cref="Charting.Lint.IChartLintRule"/>. IDs are
/// stable: a rule may be retired, but its ID is never reused.
/// </summary>
/// <remarks>
/// The timing, 5-fret and song rules came with CM2, the drums and phrase rules with CM3, and the vocal rules with vocal
/// editing.
/// </remarks>
public static class ChrRules
{
    /// <summary>No time signature at tick 0; the games assume 4/4.</summary>
    public const string Chr001 = "CHR001";

    /// <summary>A tempo below 20 or above 400 BPM.</summary>
    public const string Chr002 = "CHR002";

    /// <summary>A note, phrase or event after the end of the audio.</summary>
    public const string Chr003 = "CHR003";

    /// <summary>A note isn't on any common grid.</summary>
    public const string Chr004 = "CHR004";

    /// <summary>Two tempo markers or two time signatures on the same tick.</summary>
    public const string Chr005 = "CHR005";

    /// <summary>A sustain runs into the next note on its lane, so the games cut it short.</summary>
    public const string Chr020 = "CHR020";

    /// <summary>
    /// A sustain is shorter than the sustain cutoff, so the games read it as a plain note. Retired: Chart Builder derives
    /// the cutoff from the chart, so the rule no longer runs.
    /// </summary>
    public const string Chr021 = "CHR021";

    /// <summary>An open note in a chord with frets.</summary>
    public const string Chr022 = "CHR022";

    /// <summary>A force HOPO or force strum that doesn't change the chord.</summary>
    public const string Chr023 = "CHR023";

    /// <summary>A forced HOPO on a chord that repeats the one before exactly.</summary>
    public const string Chr024 = "CHR024";

    /// <summary>The first note of a part is a HOPO, forced or a tap.</summary>
    public const string Chr025 = "CHR025";

    /// <summary>Two notes on the same lane closer than 1/64 of a whole note.</summary>
    public const string Chr026 = "CHR026";

    /// <summary>A tom marker that covers no note.</summary>
    public const string Chr040 = "CHR040";

    /// <summary>More than two pads at once, not counting the kick.</summary>
    public const string Chr041 = "CHR041";

    /// <summary>A drum fill overlaps a star power phrase.</summary>
    public const string Chr042 = "CHR042";

    /// <summary>A drum fill's last note isn't on every difficulty, so there's nothing to activate on there.</summary>
    public const string Chr043 = "CHR043";

    /// <summary>A roll or trill with fewer than two notes, or a trill covering only one pad.</summary>
    public const string Chr044 = "CHR044";

    /// <summary>Tom markers on 4-lane drums, which saving leaves out.</summary>
    public const string Chr045 = "CHR045";

    /// <summary>A fifth-pad note on drums that aren't 5-lane.</summary>
    public const string Chr046 = "CHR046";

    // CHR047, an accent or ghost on a kick, was retired in CM3: the games never read one, so the model can't hold one.

    /// <summary>The chart has a <c>PART DRUMS_2X</c> track, which doesn't follow edits to <c>PART DRUMS</c>.</summary>
    public const string Chr048 = "CHR048";

    /// <summary>A star power phrase with no notes in it.</summary>
    public const string Chr060 = "CHR060";

    /// <summary>A solo with no notes in it.</summary>
    public const string Chr061 = "CHR061";

    /// <summary>Two star power phrases, or two solos, overlap.</summary>
    public const string Chr062 = "CHR062";

    /// <summary>Notes inside a big rock ending, which the games skip.</summary>
    public const string Chr063 = "CHR063";

    // CHR064, a disco flip section starting or ending between notes of a chord, was retired in CM3: a mix event applies
    // to every note on its tick, so it can't.

    /// <summary>A star power phrase or solo with no notes in it on one charted difficulty, though another has some.</summary>
    public const string Chr065 = "CHR065";

    /// <summary>A part star power can be generated for has notes but no star power phrases.</summary>
    public const string Chr066 = "CHR066";

    /// <summary>A part is in the chart but has no notes on any difficulty.</summary>
    public const string Chr080 = "CHR080";

    /// <summary>A lower difficulty has more notes per second than the one above it.</summary>
    public const string Chr081 = "CHR081";

    /// <summary>The chart has no sections.</summary>
    public const string Chr082 = "CHR082";

    /// <summary>A part with notes has no <c>diff_*</c> value, or <c>-1</c>.</summary>
    public const string Chr083 = "CHR083";

    /// <summary><c>song_length</c> doesn't match the audio's length within 1 s.</summary>
    public const string Chr084 = "CHR084";

    /// <summary><c>[end]</c> is missing or before the last note.</summary>
    public const string Chr085 = "CHR085";

    /// <summary>
    /// Something in the chart file couldn't be loaded as written and was left out, as the games leave it out
    /// (<see cref="Charting.ChartLoadIssue"/>). Not a lint rule: the findings come from the load
    /// (<see cref="Charting.Lint.ChartLoadFindings"/>).
    /// </summary>
    public const string Chr086 = "CHR086";

    /// <summary>A difficulty below a part's highest charted one has no notes, so the games don't offer it.</summary>
    public const string Chr087 = "CHR087";

    /// <summary>A vocal note runs into the next one.</summary>
    public const string Chr090 = "CHR090";

    /// <summary>A vocal note has no lyric.</summary>
    public const string Chr091 = "CHR091";

    /// <summary>A vocal note is outside every phrase.</summary>
    public const string Chr092 = "CHR092";

    /// <summary>A lyric isn't on any vocal note's start.</summary>
    public const string Chr093 = "CHR093";

    /// <summary>A slide has no sung note before it in its phrase.</summary>
    public const string Chr094 = "CHR094";

    /// <summary><c>HARM2</c> or <c>HARM3</c> without <c>HARM1</c>.</summary>
    public const string Chr095 = "CHR095";

    /// <summary>Every rule ID, in order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Chr001, Chr002, Chr003, Chr004, Chr005,
        Chr020, Chr021, Chr022, Chr023, Chr024, Chr025, Chr026,
        Chr040, Chr041, Chr042, Chr043, Chr044, Chr045, Chr046, Chr048,
        Chr060, Chr061, Chr062, Chr063, Chr065, Chr066,
        Chr080, Chr081, Chr082, Chr083, Chr084, Chr085, Chr086, Chr087,
        Chr090, Chr091, Chr092, Chr093, Chr094, Chr095,
    ];

    /// <summary>Whether <paramref name="ruleId"/> is a chart lint rule's: CHR and three digits.</summary>
    public static bool IsChartRule(string ruleId) => CategoryOf(ruleId) is not null;

    /// <summary>The group of the catalogue <paramref name="ruleId"/> is in, by its number; null for an ID that isn't a CHR one.</summary>
    public static ChrCategory? CategoryOf(string ruleId)
    {
        if (ruleId is not { Length: 6 } || !ruleId.StartsWith("CHR", StringComparison.Ordinal)
            || !int.TryParse(ruleId.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
            return null;
        return number switch
        {
            < 20 => ChrCategory.Timing,
            < 40 => ChrCategory.Frets,
            < 60 => ChrCategory.Drums,
            < 80 => ChrCategory.Phrases,
            < 86 => ChrCategory.Song,
            > 86 and < 90 => ChrCategory.Song,
            >= 90 and < 110 => ChrCategory.Vocals,
            _ => ChrCategory.Load,
        };
    }
}
