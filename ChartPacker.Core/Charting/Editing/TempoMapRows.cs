using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>
/// A row of the Tempo map tab: a tick where the tempo or the time signature changes, what's placed there, and what's in
/// force there either way.
/// </summary>
/// <param name="Tick">The tick.</param>
/// <param name="Tempo">The tempo marker there, the last when a loaded chart has more than one; null when there's none.</param>
/// <param name="Signature">The time signature there, the last when there's more than one; null when there's none.</param>
/// <param name="IsAnchored">Whether the tempo marker there is anchored.</param>
/// <param name="TempoInForce">The tempo from the tick on, in microseconds per quarter note, as the games read it.</param>
/// <param name="SignatureInForce">The time signature from the tick on, as the games read it.</param>
public readonly record struct TempoMapRow(
    long Tick,
    TempoChange? Tempo,
    TimeSignatureMarker? Signature,
    bool IsAnchored,
    long TempoInForce,
    TimeSignature SignatureInForce)
{
    public bool HasTempo => Tempo is not null;

    public bool HasSignature => Signature is not null;
}

/// <summary>
/// The rows of the Tempo map tab: one for each tick with a tempo marker or a time signature, in tick order, and one for the
/// song's start whether or not anything's placed there. The games read the last of the markers at a tick, so that's the
/// one a row shows.
/// </summary>
public static class TempoMapRows
{
    public static IReadOnlyList<TempoMapRow> Of(EditableTempoMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var tempos = map.Tempos.AsSpan();
        var signatures = map.TimeSignatures.AsSpan();
        var tidied = map.ToTempoMap();
        var rows = new List<TempoMapRow>(tempos.Length + signatures.Length + 1);
        int t = 0, s = 0, inForceTempo = 0, inForceSignature = 0;
        long tick = 0;
        while (true)
        {
            TempoChange? tempo = null;
            while (t < tempos.Length && tempos[t].Tick == tick)
                tempo = tempos[t++];
            TimeSignatureMarker? signature = null;
            while (s < signatures.Length && signatures[s].Tick == tick)
                signature = signatures[s++];

            while (inForceTempo + 1 < tidied.Tempos.Count && tidied.Tempos[inForceTempo + 1].Tick <= tick)
                inForceTempo++;
            while (inForceSignature + 1 < tidied.TimeSignatures.Count && tidied.TimeSignatures[inForceSignature + 1].Tick <= tick)
                inForceSignature++;

            rows.Add(new TempoMapRow(
                tick,
                tempo,
                signature,
                tempo is not null && !map.Anchors.At(tick).IsEmpty,
                tidied.Tempos[inForceTempo].MicrosecondsPerQuarter,
                tidied.TimeSignatures[inForceSignature]));

            long next = long.MaxValue;
            if (t < tempos.Length)
                next = tempos[t].Tick;
            if (s < signatures.Length)
                next = Math.Min(next, signatures[s].Tick);
            if (next == long.MaxValue)
                return rows;
            tick = next;
        }
    }

    /// <summary>
    /// The index of the row in force at <paramref name="tick"/>: the last at or before it, or -1 when
    /// <paramref name="rows"/> is empty.
    /// </summary>
    public static int IndexAt(IReadOnlyList<TempoMapRow> rows, double tick)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int lo = 0, hi = rows.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (rows[mid].Tick <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return rows.Count == 0 ? -1 : Math.Max(0, lo - 1);
    }
}
