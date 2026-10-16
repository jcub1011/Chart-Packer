using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;

namespace ChartBuilder.Tests.Playtest;

/// <summary>Game-view tracks and timed actions for the judge's tests: 960 ticks a second (480 a beat at 120 BPM).</summary>
internal static class Plays
{
    public const double TicksPerSecond = 960;

    public static long TickAt(double seconds) => (long)Math.Round(seconds * TicksPerSecond);

    /// <summary>A note at <paramref name="seconds"/>, held for <paramref name="holdSeconds"/>.</summary>
    public static ChartNote Note(double seconds, int lane, NoteFlags flags = NoteFlags.None, double holdSeconds = 0) =>
        new(TickAt(seconds), TickAt(holdSeconds), lane, flags);

    public static PlaytestChart Frets(params ChartNote[] notes) =>
        PlaytestChart.Frets(new NoteTrack(notes, []), static t => t / TicksPerSecond, static s => s * TicksPerSecond);

    public static PlaytestChart Drums(DrumsVariant variant, params ChartNote[] notes) =>
        PlaytestChart.DrumNotes(new NoteTrack(notes, []), static t => t / TicksPerSecond, static s => s * TicksPerSecond, variant, includeDoubleKick: true);

    public static TimedAction Down(InstrumentAction action, double song, double speed = 1, HitStrength strength = HitStrength.Normal) =>
        new(new ActionEvent(action, true, song, InputDeviceId.Keyboard, strength), song, speed);

    public static TimedAction Up(InstrumentAction action, double song, double speed = 1) =>
        new(new ActionEvent(action, false, song, InputDeviceId.Keyboard), song, speed);

    /// <summary>Feeds <paramref name="actions"/> from the start of the song, then judges up to <paramref name="end"/>.</summary>
    public static List<JudgeEvent> Play(IPlaytestEngine engine, double end, params TimedAction[] actions)
    {
        var output = new List<JudgeEvent>();
        engine.Reset(0, actions.Length > 0 ? actions[0].Speed : 1, static _ => false);
        foreach (var action in actions)
            engine.Input(action, output);
        engine.Advance(end, actions.Length > 0 ? actions[^1].Speed : 1, output);
        return output;
    }

    /// <summary>What was judged, without ghosts and finished sustains.</summary>
    public static List<(JudgeKind Kind, int Note)> Kinds(List<JudgeEvent> judged) =>
        [.. judged.Where(static j => j.Kind is not (JudgeKind.Ghost or JudgeKind.SustainDone)).Select(static j => (j.Kind, j.Note))];
}
