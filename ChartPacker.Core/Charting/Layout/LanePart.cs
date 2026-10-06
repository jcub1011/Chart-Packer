using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Layout;

/// <summary>A vocal part an editor lane can show, one per track <c>VocalReader</c> reads.</summary>
public enum VocalTrack
{
    /// <summary><c>PART VOCALS</c>.</summary>
    Vocals,

    /// <summary><c>HARM1</c>.</summary>
    Harmony1,

    /// <summary><c>HARM2</c>.</summary>
    Harmony2,

    /// <summary><c>HARM3</c>.</summary>
    Harmony3,
}

/// <summary>What an editor lane shows: one instrument's highway at one difficulty, or one vocal part.</summary>
public sealed record LanePart
{
    private LanePart(ChartTrackKey? track, VocalTrack? vocal)
    {
        Track = track;
        Vocal = vocal;
    }

    /// <summary>The highway's instrument and difficulty, or null for a vocal part.</summary>
    public ChartTrackKey? Track { get; }

    /// <summary>The vocal part, or null for a highway.</summary>
    public VocalTrack? Vocal { get; }

    public bool IsVocal => Vocal is not null;

    public static LanePart Highway(ChartTrackKey track) => new(track, null);

    public static LanePart Highway(ChartInstrument instrument, ChartDifficulty difficulty) => new(new ChartTrackKey(instrument, difficulty), null);

    public static LanePart Vocals(VocalTrack track) => new(null, track);
}
