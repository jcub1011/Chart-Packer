namespace ChartShared.Core.Charting.Editing;

// The tempo map's changes (charter-spec C4): what the Tempo tool takes hold of on the lanes, and what the Tempo map tab's
// add buttons and Ctrl+Shift+↑ and Ctrl+Shift+↓ do.
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

    /// <summary>
    /// The tick of the tempo or time signature change nearest <paramref name="point"/>, within its tolerance; null for none.
    /// A tempo marker and a time signature at one tick are one change.
    /// </summary>
    public long? TempoChangeAt(ChartPoint point) => TempoChangeAt(point.Tick, point.Tolerance);

    /// <summary>
    /// The tick of the tempo or time signature change nearest <paramref name="tick"/>, within
    /// <paramref name="tolerance"/> ticks of it; null for none. A tempo marker and a time signature at one tick are one
    /// change.
    /// </summary>
    public long? TempoChangeAt(double tick, double tolerance) => ChangeNear(tick, tolerance);

    /// <summary>Whether the change at <paramref name="tick"/> can be moved: the one the song starts with stays put.</summary>
    public static bool TempoChangeMoves(long tick) => tick > 0;

    /// <summary>
    /// The grid step where a press of the Tempo tool at <paramref name="point"/> would add a tempo marker: the nearest,
    /// when there's no change there and the press isn't in the phrase rail. Null where a press would drag a change
    /// instead, or would find a marker already there.
    /// </summary>
    public long? TempoAddAt(ChartPoint point) => point.Rail is null ? TempoAddAt(point.Tick, point.Tolerance) : null;

    /// <summary>
    /// The grid step where a press at <paramref name="tick"/> would add a tempo marker, within
    /// <paramref name="tolerance"/> ticks of it: the nearest, when no change is there and it isn't before the song's
    /// start. Null where a press would drag a change instead, or would find a marker already there.
    /// </summary>
    public long? TempoAddAt(double tick, double tolerance)
    {
        if (ChangeNear(tick, tolerance) is not null)
            return null;
        long at = Snap.Nearest(tick, Map);
        return at < 0 || !Chart.TempoMap.Tempos.At(at).IsEmpty ? null : at;
    }

    // The tick of the nearest change, tempo marker or time signature, within the tolerance.
    private long? ChangeNear(double pressed, double tolerance)
    {
        long from = (long)Math.Floor(pressed - tolerance), to = (long)Math.Ceiling(pressed + tolerance);
        long? best = null;
        double bestDistance = double.MaxValue;

        // The tempo markers first, so one at the same distance as a time signature wins.
        foreach (var marker in Chart.TempoMap.Tempos.Between(Math.Max(0, from), to))
        {
            double distance = Math.Abs(marker.Tick - pressed);
            if (distance < bestDistance)
                (best, bestDistance) = (marker.Tick, distance);
        }

        foreach (var signature in Chart.TempoMap.TimeSignatures.Between(Math.Max(0, from), to))
        {
            double distance = Math.Abs(signature.Tick - pressed);
            if (distance < bestDistance)
                (best, bestDistance) = (signature.Tick, distance);
        }

        return best;
    }

    // The cursor's tick, to the nearest, rather than on the grid.
    private long CursorTickUnsnapped() => (long)Math.Round(Math.Max(0, _session.TickAt(_session.Viewport.Cursor)));
}
