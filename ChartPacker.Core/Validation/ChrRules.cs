namespace ChartPacker.Core.Validation;

/// <summary>
/// Rule IDs from the chart lint rule catalogue (charter-spec §6), for <see cref="Charting.Lint.IChartLintRule"/>. IDs are
/// stable: a rule may be retired, but its ID is never reused.
/// </summary>
/// <remarks>The timing, 5-fret and song rules came with CM2; the drums and phrase rules come with CM3.</remarks>
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

    /// <summary>A sustain is shorter than the sustain cutoff, so the games read it as a plain note.</summary>
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

    /// <summary>A part has notes on some difficulties and none on others.</summary>
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
}
