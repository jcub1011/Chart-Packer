using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>The <c>notes.mid</c> a new chart starts with (charter-spec C1).</summary>
/// <remarks>
/// A placeholder until the editable model and its writer exist (milestone CM1): for now the file is
/// a header at the chosen resolution and one empty track, which <see cref="MidiChartReader"/> reads
/// as an empty chart. CM1 replaces the body with the writer run over a new editable chart built from
/// the <see cref="NewChartSettings"/>: the tempo track named after the song with the starting tempo and time
/// signature, an empty track for each part, <c>EVENTS</c> holding <c>[music_start]</c> and
/// <c>[end]</c>, and <c>BEAT</c>.
/// </remarks>
public static class NewChartMidi
{
    /// <summary>The new chart's <c>notes.mid</c>.</summary>
    /// <param name="settings">The new chart's timing and parts.</param>
    public static byte[] Build(NewChartSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MidiWriter.WriteFile(settings.Resolution, [MidiWriter.Track([])]);
    }
}
