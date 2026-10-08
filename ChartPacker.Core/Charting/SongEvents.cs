using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>The song events of <c>EVENTS</c> (charter-spec C8 items 4 and 5, §7.3).</summary>
public enum SongEvent
{
    Coda,
    MusicStart,
    MusicEnd,
    End,
}

/// <summary>
/// The song events of <c>EVENTS</c> (charter-spec C8 items 4 and 5): <c>[coda]</c>, <c>[music_start]</c>,
/// <c>[music_end]</c> and <c>[end]</c>, as the games read them.
/// </summary>
public static class SongEvents
{
    /// <summary>The song events, in the order <b>Insert event</b> offers them.</summary>
    public static IReadOnlyList<SongEvent> All { get; } = [SongEvent.Coda, SongEvent.MusicStart, SongEvent.MusicEnd, SongEvent.End];

    /// <summary>The text of <paramref name="songEvent"/>, brackets included.</summary>
    public static string TextOf(SongEvent songEvent) => songEvent switch
    {
        SongEvent.Coda => "[coda]",
        SongEvent.MusicStart => "[music_start]",
        SongEvent.MusicEnd => "[music_end]",
        _ => ChartSaveFixups.EndText,
    };

    /// <summary>The song event <paramref name="text"/> is, or null when it isn't one.</summary>
    public static SongEvent? KindOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ChartTextEvents.Normalize(text) switch
        {
            "coda" => SongEvent.Coda,
            "music_start" => SongEvent.MusicStart,
            "music_end" => SongEvent.MusicEnd,
            "end" => SongEvent.End,
            _ => null,
        };
    }

    /// <summary>
    /// Whether a song has only one <paramref name="songEvent"/>, so the editor refuses a second: <c>[music_start]</c>,
    /// <c>[music_end]</c> and <c>[end]</c>.
    /// </summary>
    public static bool IsOnePerSong(SongEvent songEvent) => songEvent != SongEvent.Coda;
}
