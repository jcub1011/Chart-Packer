namespace ChartPacker.Core.Charting.Playtest;

/// <summary>A pass's or a session's counts (charter-spec C19 item 4): what the transport bar shows.</summary>
public sealed class PlaytestStats
{
    private double _meanOffset;
    private double _offsetSquares;

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    /// <summary>Overstrums and overhits.</summary>
    public int Overhits { get; private set; }

    public int SustainDrops { get; private set; }

    public int WrongDynamics { get; private set; }

    /// <summary>Hits before the note.</summary>
    public int Early { get; private set; }

    /// <summary>Hits after the note.</summary>
    public int Late { get; private set; }

    public int Combo { get; private set; }

    public int BestCombo { get; private set; }

    /// <summary>Notes hit out of notes judged, 0 to 1; 1 before any.</summary>
    public double Accuracy => Hits + Misses == 0 ? 1 : (double)Hits / (Hits + Misses);

    /// <summary>The hits' mean offset in real milliseconds, early negative; 0 before any.</summary>
    public double MeanOffsetMs => _meanOffset;

    /// <summary>How spread the hits' offsets are, in milliseconds; 0 before two.</summary>
    public double SpreadMs => Hits < 2 ? 0 : Math.Sqrt(_offsetSquares / (Hits - 1));

    /// <summary>Counts <paramref name="judged"/>.</summary>
    public void Add(in JudgeEvent judged)
    {
        switch (judged.Kind)
        {
            case JudgeKind.Hit:
                Hits++;
                if (judged.OffsetMs < 0)
                    Early++;
                else if (judged.OffsetMs > 0)
                    Late++;

                // Welford's running mean and sum of squares.
                double delta = judged.OffsetMs - _meanOffset;
                _meanOffset += delta / Hits;
                _offsetSquares += delta * (judged.OffsetMs - _meanOffset);
                break;
            case JudgeKind.Miss:
                Misses++;
                break;
            case JudgeKind.Overstrum or JudgeKind.Overhit:
                Overhits++;
                break;
            case JudgeKind.SustainDrop:
                SustainDrops++;
                break;
            case JudgeKind.WrongDynamics:
                WrongDynamics++;
                break;
        }

        Combo = judged.Combo;
        BestCombo = Math.Max(BestCombo, judged.Combo);
    }
}
