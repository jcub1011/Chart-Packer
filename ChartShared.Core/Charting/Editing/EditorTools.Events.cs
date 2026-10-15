using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

// Sections and the other text events (charter-spec C8 items 3, 5 and 6): what the Sections tab and Ctrl+↑ and Ctrl+↓ do.
public sealed partial class EditorTools
{
    /// <summary>The text of a text event inserted to be renamed.</summary>
    public const string NewEventText = "[event]";

    // How far into the song a text event can go, at the least: an hour of chart time.
    private const double LatestEventSeconds = 3600;

    // How far past the chart's last note, event or tempo marker a text event can go, when that's later: 64 measures of
    // 4/4, in beats.
    private const long LatestEventBeatsPastEnd = 64 * 4;

    private const string NoSectionName = "A section needs a name.";
    private const string BracketInName = "A section's name can't have [ or ] in it, as the event's own brackets end at the first ].";
    private const string NoEventText = "A text event needs some text. To take it away, delete it.";
    private const string EventBeforeStart = "A text event can't be before the start of the song.";
    private const string SectionAtTick = "There's a section at this tick already. Rename that one, or put this one somewhere else.";

    /// <summary>
    /// Moves the cursor to the start of the next section after it, or of the one before it when <paramref name="sections"/>
    /// is negative, <paramref name="sections"/> sections on (charter-spec §9: Ctrl+↑ and Ctrl+↓). Sections at one tick are
    /// one start. With none that way, the cursor stays where it is, and the status says so.
    /// </summary>
    public void StepCursorSections(int sections)
    {
        if (sections == 0)
            return;
        long tick = (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
        var starts = new List<long>();
        foreach (var e in Chart.Events)
        {
            if (SectionEvents.IsSection(e.Text) && (starts.Count == 0 || starts[^1] != e.Tick))
                starts.Add(e.Tick);
        }

        int index;
        if (sections > 0)
        {
            int after = starts.FindIndex(t => t > tick);
            index = after < 0 ? -1 : after + sections - 1;
        }
        else
        {
            int before = starts.FindLastIndex(t => t < tick);
            index = before < 0 ? -1 : before + sections + 1;
        }

        if (index < 0 || index >= starts.Count)
        {
            _session.Status = sections > 0 ? "There's no section after the cursor." : "There's no section before the cursor.";
            return;
        }

        _session.MoveCursorTo(starts[index]);
    }

    /// <summary>
    /// Inserts a section named <paramref name="name"/>, <c>[section &lt;name&gt;]</c>, in <c>EVENTS</c> at the cursor on the
    /// grid (charter-spec C8 item 3), as one step. Where there's a section already, it's refused.
    /// </summary>
    /// <returns>The section inserted, or null when it was refused, with the status saying why.</returns>
    public TextEvent? InsertSection(string name)
    {
        if (SectionNameRefusal(name) is { } why)
        {
            _session.Status = why;
            return null;
        }

        return InsertEvent(null, SectionEvents.TextFor(name));
    }

    /// <summary>
    /// Inserts a text event in <c>EVENTS</c>, or on <paramref name="part"/>'s track, at the cursor on the grid (charter-spec
    /// C8 items 5 and 6), as one step. In <c>EVENTS</c>, a section where there's one already, or a <c>[music_start]</c>,
    /// <c>[music_end]</c> or <c>[end]</c>, which are placed (<see cref="SongEventPlacement"/>), is refused; a <c>[coda]</c>
    /// that becomes the first makes the drum fills after it big rock endings in the same step (item 4).
    /// </summary>
    /// <param name="part">The part whose track it goes on, or null for <c>EVENTS</c>.</param>
    /// <param name="text">Its text, brackets included.</param>
    /// <returns>The event inserted, or null when it was refused, with the status saying why.</returns>
    public TextEvent? InsertEvent(ChartInstrument? part, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _session.Status = NoEventText;
            return null;
        }

        var inserted = TextEvent.Of(_session.CursorTick, text.Trim());
        if ((TickRefusal(inserted.Tick) ?? EventRefusal(part, inserted, leaving: null)) is { } why)
        {
            _session.Status = why;
            return null;
        }

        return _session.Execute(new AddTextEvent(part, inserted)) ? inserted : null;
    }

    /// <summary>
    /// Renames <paramref name="section"/> to <paramref name="name"/>, keeping its style, <c>[section …]</c> or
    /// <c>[prc_…]</c>, as one step. An empty name, or one with a bracket, is refused.
    /// </summary>
    /// <returns>The section as it now is, or null when nothing changed, with the status saying why when it was refused.</returns>
    public TextEvent? RenameSection(TextEvent section, string name)
    {
        if (SectionNameRefusal(name) is { } why)
        {
            _session.Status = why;
            return null;
        }

        return ChangeEvent(null, section, section with { Text = SectionEvents.TextFor(name, section.Text) });
    }

    /// <summary>
    /// Changes the text of <paramref name="textEvent"/>, in <c>EVENTS</c> or on <paramref name="part"/>'s track, to
    /// <paramref name="text"/>, brackets and all, as one step. Empty text is refused, as are the refusals of
    /// <see cref="InsertEvent"/> for what it becomes.
    /// </summary>
    /// <returns>The event as it now is, or null when nothing changed, with the status saying why when it was refused.</returns>
    public TextEvent? ChangeEventText(ChartInstrument? part, TextEvent textEvent, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _session.Status = NoEventText;
            return null;
        }

        return ChangeEvent(part, textEvent, textEvent with { Text = text.Trim() });
    }

    /// <summary>
    /// Moves <paramref name="textEvent"/>, in <c>EVENTS</c> or on <paramref name="part"/>'s track, to
    /// <paramref name="tick"/>, as one step. Moving a section onto another is refused, as is a tick before the start or far
    /// past the end of the song; moving <c>[coda]</c> makes the drum fills and big rock endings what it then decides, in the
    /// same step.
    /// </summary>
    /// <returns>The event as it now is, or null when nothing changed, with the status saying why when it was refused.</returns>
    public TextEvent? MoveEvent(ChartInstrument? part, TextEvent textEvent, long tick)
    {
        if (TickRefusal(tick) is { } why)
        {
            _session.Status = why;
            return null;
        }

        return ChangeEvent(part, textEvent, textEvent with { Tick = tick });
    }

    /// <summary>Deletes <paramref name="textEvent"/> from <c>EVENTS</c> or <paramref name="part"/>'s track, as one step.</summary>
    /// <returns>Whether it was deleted.</returns>
    public bool DeleteEvent(ChartInstrument? part, TextEvent textEvent) => _session.Execute(new RemoveTextEvent(part, textEvent));

    /// <summary>Changes <paramref name="from"/> to <paramref name="to"/>, unless they're the same or it's refused.</summary>
    private TextEvent? ChangeEvent(ChartInstrument? part, TextEvent from, TextEvent to)
    {
        if (from == to)
            return null;
        if (EventRefusal(part, to, leaving: from) is { } why)
        {
            _session.Status = why;
            return null;
        }

        return _session.Execute(new ChangeTextEvent(part, from, to)) ? to : null;
    }

    /// <summary>
    /// Why <paramref name="textEvent"/> can't be in <c>EVENTS</c> (when <paramref name="part"/> is null) as well as what's
    /// there: it would stack a section on one at its tick, or be a <c>[music_start]</c>, <c>[music_end]</c> or
    /// <c>[end]</c>, which are placed, never edited (<see cref="SongEventPlacement"/>). Null when it can, and always on a
    /// part's track, whose text events are free text.
    /// </summary>
    /// <remarks>
    /// Only a duplicate section the edit makes is refused. A section already at the tick that is <paramref name="leaving"/>
    /// leaves as many as there were, so a loaded chart's pair of sections at one tick can still be renamed and moved.
    /// </remarks>
    /// <param name="leaving">The event it replaces, or null for one added.</param>
    private string? EventRefusal(ChartInstrument? part, TextEvent textEvent, TextEvent? leaving)
    {
        if (part is not null)
            return null;

        if (SectionEvents.IsSection(textEvent.Text) && !(leaving is { } old && old.Tick == textEvent.Tick && SectionEvents.IsSection(old.Text)))
        {
            foreach (var other in Chart.Events.At(textEvent.Tick))
            {
                if (SectionEvents.IsSection(other.Text))
                    return SectionAtTick;
            }
        }

        if (SongEventPlacement.IsPlaced(textEvent) && SongEvents.KindOf(textEvent.Text) is { } kind)
            return FormattableString.Invariant($"{SongEvents.TextOf(kind)} is placed automatically from the audio and the notes.");

        return null;
    }

    /// <summary>Why a section can't be named <paramref name="name"/>: it's empty, or has a bracket. Null when it can.</summary>
    private static string? SectionNameRefusal(string name) =>
        string.IsNullOrWhiteSpace(name) ? NoSectionName
        : name.AsSpan().IndexOfAny('[', ']') >= 0 ? BracketInName
        : null;

    /// <summary>Why a text event can't be at <paramref name="tick"/>: before the start, or past <see cref="LatestEventTick"/>. Null when it can.</summary>
    private string? TickRefusal(long tick)
    {
        if (tick < 0)
            return EventBeforeStart;
        long latest = LatestEventTick();
        return tick > latest
            ? FormattableString.Invariant($"Tick {tick} is too far past the end of the song for a text event; the latest is {latest}.")
            : null;
    }

    /// <summary>
    /// The latest tick a text event can be put at: an hour of chart time in, or 64 measures of 4/4 past the chart's last
    /// note, event or tempo marker when that's later.
    /// </summary>
    /// <remarks>
    /// It catches a tick typed with a digit too many, which no song reaches; a song a game plays is well under an hour.
    /// It follows the chart, rather than being a fixed number, so a loaded chart whose events run later than an hour can
    /// still have them moved.
    /// </remarks>
    private long LatestEventTick()
    {
        long last = SongEventPlacement.LastNoteEnd(Chart) ?? 0;
        if (Chart.Events.Count > 0)
            last = Math.Max(last, Chart.Events[^1].Tick);
        foreach (var part in Chart.Parts)
        {
            if (part.TextEvents.Count > 0)
                last = Math.Max(last, part.TextEvents[^1].Tick);
        }

        var tempos = Chart.TempoMap.Tempos;
        if (tempos.Count > 0)
            last = Math.Max(last, tempos[^1].Tick);
        long hour = (long)Math.Ceiling(Map.TickAt(LatestEventSeconds));
        return Math.Max(hour, last + (LatestEventBeatsPastEnd * Chart.Resolution));
    }
}
