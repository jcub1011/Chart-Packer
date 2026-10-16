namespace ChartShared.Core.Charting.Editing;

// The tempo map's changes (charter-spec C4): what the Tempo map tab's add buttons and Ctrl+Shift+↑ and Ctrl+Shift+↓ do.
public sealed partial class EditorTools
{
    /// <summary>
    /// Moves the cursor to the next tick after it where the tempo or the time signature changes, or to the one before it
    /// when <paramref name="steps"/> is negative, <paramref name="steps"/> changes on (Ctrl+Shift+↑ and Ctrl+Shift+↓). A
    /// tempo marker and a time signature at one tick are one change. With none that way, the cursor stays where it is, and
    /// the status says so.
    /// </summary>
    public void StepCursorTempoChanges(int steps)
    {
        if (steps == 0)
            return;
        long tick = CursorTickUnsnapped();
        var changes = TempoMapRows.Of(Chart.TempoMap).Select(static r => r.Tick).ToList();

        int index;
        if (steps > 0)
        {
            int after = changes.FindIndex(t => t > tick);
            index = after < 0 ? -1 : after + steps - 1;
        }
        else
        {
            int before = changes.FindLastIndex(t => t < tick);
            index = before < 0 ? -1 : before + steps + 1;
        }

        if (index < 0 || index >= changes.Count)
        {
            _session.Status = steps > 0 ? "There's no tempo or time signature change after the cursor." : "There's no tempo or time signature change before the cursor.";
            return;
        }

        _session.MoveCursorTo(changes[index]);
    }

    /// <summary>
    /// Adds a tempo marker at the cursor, on the grid, with the tempo in force there, and selects it, as one step. Where
    /// there's a marker already, it's refused.
    /// </summary>
    /// <returns>The marker's tick, or null when it was refused, with the status saying why.</returns>
    public long? AddTempoChangeAtCursor()
    {
        long tick = _session.CursorTick;
        var edit = TempoMapEdits.AddMarker(Chart, tick);
        if (edit.IsRefused)
        {
            _session.Status = edit.Refusal;
            return null;
        }

        if (!_session.Execute(edit.Command!))
            return null;
        _session.Selection.SelectTempos([tick]);
        return tick;
    }

    /// <summary>
    /// Puts a time signature of <paramref name="numerator"/>/<paramref name="denominator"/> at the start of the measure
    /// the cursor is in, as one step: the one there is changed, or one is added.
    /// </summary>
    /// <returns>The signature's tick, or null when nothing changed, with the status saying why.</returns>
    public long? PlaceSignatureAtCursor(int numerator, int denominator)
    {
        long tick = MeasureBeat.MeasureStart(Map, CursorTickUnsnapped());
        var edit = TempoMapEdits.PlaceSignature(Chart, tick, numerator, denominator);
        if (edit.IsRefused)
        {
            _session.Status = edit.Refusal;
            return null;
        }

        return _session.Execute(edit.Command!) ? tick : null;
    }

    // The cursor's tick, to the nearest, rather than on the grid.
    private long CursorTickUnsnapped() => (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
}
