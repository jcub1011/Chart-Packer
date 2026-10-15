using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Layout;

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

/// <summary>The MIDI tracks <see cref="VocalTrack"/>'s parts are.</summary>
public static class VocalTracks
{
    /// <summary>The name of <paramref name="track"/>'s MIDI track: <c>PART VOCALS</c>, or <c>HARM1</c> to <c>HARM3</c>.</summary>
    public static string NameOf(VocalTrack track) => VocalReader.TrackNames[(int)track];

    /// <summary>
    /// The index in <paramref name="chart"/>'s passthrough tracks of <paramref name="track"/>'s part: the first track of
    /// its name, ignoring case and surrounding space, as the reader takes it; -1 when the chart hasn't one.
    /// </summary>
    public static int IndexIn(EditableChart chart, VocalTrack track)
    {
        ArgumentNullException.ThrowIfNull(chart);
        string name = NameOf(track);
        for (int i = 0; i < chart.Passthrough.Count; i++)
        {
            var each = chart.Passthrough[i];
            if (each.IsTrack && each.Name is { } eachName && eachName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>The vocal part a track of <paramref name="name"/> is, ignoring case and surrounding space; null for any other track.</summary>
    public static VocalTrack? Of(string? name)
    {
        if (name is null)
            return null;
        for (int i = 0; i < VocalReader.TrackNames.Count; i++)
        {
            if (VocalReader.TrackNames[i].Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                return (VocalTrack)i;
        }

        return null;
    }
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
