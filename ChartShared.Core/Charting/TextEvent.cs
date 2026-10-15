namespace ChartShared.Core.Charting;

/// <summary>
/// A text event on a track: a section, a song event such as <c>[music_start]</c> or <c>[end]</c>, or free text
/// (charter-spec §4.1).
/// </summary>
/// <param name="Tick">Where it is, in chart ticks.</param>
/// <param name="Type">Its MIDI meta-event type: <see cref="TextType"/> for most, or another text type such as a lyric or marker.</param>
/// <param name="Text">Its text, exactly as written, brackets included.</param>
public readonly record struct TextEvent(long Tick, byte Type, string Text)
{
    /// <summary>The meta-event type of a plain text event.</summary>
    public const byte TextType = 0x01;

    /// <summary>A plain text event.</summary>
    public static TextEvent Of(long tick, string text) => new(tick, TextType, text);
}
