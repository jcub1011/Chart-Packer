namespace ChartShared.Core.Charting.Playtest;

/// <summary>
/// How far either side of a note it can be hit, in real milliseconds (charter-spec C19 items 5 and 6). At a speed other
/// than 100% it's as long in real time, so it covers that much less or more of the song.
/// </summary>
public sealed record HitWindow(double EarlyMs = HitWindow.DefaultMs, double LateMs = HitWindow.DefaultMs)
{
    public const double DefaultMs = 70;
    public const double MinMs = 20;
    public const double MaxMs = 200;

    /// <summary>The early side in song seconds at <paramref name="speed"/>.</summary>
    public double Early(double speed) => EarlyMs / 1000 * speed;

    /// <summary>The late side in song seconds at <paramref name="speed"/>.</summary>
    public double Late(double speed) => LateMs / 1000 * speed;
}

/// <summary>
/// The 5-fret rules (charter-spec C19 item 5), with the times in real milliseconds. The defaults are close to the games'
/// for checking a chart; they aren't any one game's exactly.
/// </summary>
/// <param name="StrumLeniencyMs">How long a strum waits for the frets to be right before it's an overstrum.</param>
/// <param name="HopoLeniencyMs">How long after a HOPO or tap hit by the frets a strum is part of the same hit.</param>
/// <param name="AntiGhosting">Whether pressing a fret that's wrong for the next HOPO or tap means it must be strummed.</param>
/// <param name="HopoNeedsCombo">Whether a HOPO is hit by the frets only while the combo is going (a tap always is).</param>
/// <param name="SustainDropLeniencyMs">How early a sustain can be let go of without being dropped.</param>
public sealed record FretRules(
    double StrumLeniencyMs = 50,
    double HopoLeniencyMs = 80,
    bool AntiGhosting = true,
    bool HopoNeedsCombo = true,
    double SustainDropLeniencyMs = 50);

/// <summary>The drums rules (charter-spec C19 item 6).</summary>
/// <param name="CymbalsMustMatch">On Pro drums, whether a cymbal note needs a cymbal and a tom note a tom, for the colours bound apart.</param>
/// <param name="IncludeDoubleKick">Whether Expert's 2× kick notes are judged.</param>
/// <param name="OverhitBreaksCombo">Whether a pad hit with no note to hit breaks the combo.</param>
/// <param name="JudgeDynamics">Whether an accent hit softly or a ghost hit hard is counted.</param>
public sealed record DrumRules(
    bool CymbalsMustMatch = true,
    bool IncludeDoubleKick = true,
    bool OverhitBreaksCombo = true,
    bool JudgeDynamics = false);
