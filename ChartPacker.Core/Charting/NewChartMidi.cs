namespace ChartPacker.Core.Charting;

/// <summary>The <c>notes.mid</c> a new chart starts with (charter-spec C1).</summary>
/// <remarks>
/// The writer run over the new chart <see cref="EditableChart.CreateNew"/> builds from the
/// <see cref="NewChartSettings"/>: the tempo track named after the song with the starting tempo and time signature, an
/// empty track for each part, <c>EVENTS</c> holding <c>[music_start]</c> and <c>[end]</c>, and <c>BEAT</c>.
/// </remarks>
public static class NewChartMidi
{
    /// <summary>The new chart's <c>notes.mid</c>.</summary>
    /// <param name="settings">The new chart's timing and parts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The resolution, tempo or time signature can't be written.</exception>
    public static byte[] Build(NewChartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MidiChartWriter.Write(EditableChart.CreateNew(settings));
    }
}
