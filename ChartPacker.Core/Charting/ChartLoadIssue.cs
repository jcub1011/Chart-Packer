using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>
/// What <see cref="MidiChartLoader"/> couldn't take into the model as written. Each is something the games don't read
/// either, or read differently from how the model could hold it, so leaving it out keeps the chart playing as it did.
/// </summary>
public enum ChartLoadIssueKind
{
    /// <summary>A tempo marker of 0 microseconds per beat, which the model can't hold and the games skip: left out.</summary>
    UnholdableTempo,

    /// <summary>A time signature of 0 beats, which the model can't hold and the games skip: left out.</summary>
    UnholdableTimeSignature,

    /// <summary>
    /// A <c>[chartpacker_anchor]</c> event that isn't at a tempo marker's tick, or a second one at a tick: left out, as
    /// it anchors nothing.
    /// </summary>
    StrayAnchor,

    /// <summary>
    /// A part track's first event, which the games never read, wasn't the track's name: left out, as written after the
    /// name it would start being read.
    /// </summary>
    FirstEventDropped,

    /// <summary>A note at a tick and lane that already has one, which the games leave out: left out.</summary>
    DuplicateNote,

    /// <summary>A note that a Phase Shift open phrase makes open where its chord already has an open note: left out.</summary>
    DuplicateOpen,

    /// <summary>A five-fret note below green let go before <c>[ENHANCED_OPENS]</c>, which the games don't read: left out.</summary>
    OpenBeforeEnhancedOpens,

    /// <summary>
    /// A note-on or note-off at a pitch the model reads that the games pair with nothing: a second note-on while one is
    /// held, a note-off with none held, or a note never let go. Left out, as written back it could pair with a note.
    /// </summary>
    UnpairedNoteEvent,

    /// <summary>A Phase Shift open or tap event that the games pair with nothing: left out, for the same reason.</summary>
    UnpairedSysexEvent,

    /// <summary>A force-HOPO, force-strum, tap or Phase Shift open marker that covers no note: left out.</summary>
    UnusedMarker,

    /// <summary>
    /// A star-power phrase (pitch 116) on a song whose <c>multiplier_note</c> puts star power on 103, which the games
    /// don't read: left out, so it doesn't become star power once the setting is cleared.
    /// </summary>
    UnreadStarPower,

    /// <summary>A second star-power phrase starting at a tick, which the games leave out: left out.</summary>
    DuplicateStarPower,
}

/// <summary>Something <see cref="MidiChartLoader"/> couldn't take into the model as written, and where it is.</summary>
/// <param name="Part">The part it's on, or null for the tempo track.</param>
/// <param name="Difficulty">The difficulty it's on, or null when it's on every difficulty or none.</param>
/// <param name="Tick">Where it is, in the chart's ticks, as the file has it.</param>
/// <param name="Pitch">The pitch of the note event, if it's one.</param>
public sealed record ChartLoadIssue(ChartLoadIssueKind Kind, ChartInstrument? Part, ChartDifficulty? Difficulty, long Tick, int? Pitch = null);

/// <summary>A <c>notes.mid</c> read into the editable model, and what of it couldn't be taken in as written.</summary>
/// <param name="Issues">What couldn't be: the tempo track's, then each part's in <see cref="ChartInstrument"/> order.</param>
public sealed record ChartLoadResult(EditableChart Chart, IReadOnlyList<ChartLoadIssue> Issues);

/// <summary>A <c>notes.chart</c> converted to MIDI and read into the editable model (charter-spec C2 item 3).</summary>
/// <param name="Load">The converted chart, read as a <c>notes.mid</c> is.</param>
/// <param name="Offset">The <c>.chart</c>'s <c>[Song]</c> <c>Offset</c>, which song.ini's <c>delay</c> takes over.</param>
/// <param name="Options">The song.ini settings the converted chart reads the same as the <c>.chart</c> with; see <see cref="ChartConversion.Options"/>.</param>
/// <param name="Warnings">The conversion's warnings: tracks that didn't come out exactly as the <c>.chart</c> has them.</param>
public sealed record ChartImportResult(ChartLoadResult Load, TimeSpan? Offset, ChartReadOptions Options, IReadOnlyList<ChartMergeWarning> Warnings);
