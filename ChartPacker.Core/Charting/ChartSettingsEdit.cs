using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting;

/// <summary>What Chart settings sets of an open song's chart (charter-spec C1, Timing and Parts).</summary>
/// <param name="Resolution">Ticks per beat.</param>
/// <param name="MicrosecondsPerQuarter">The tempo at tick 0, as MIDI stores it.</param>
/// <param name="Numerator">The time signature at tick 0's beats per measure.</param>
/// <param name="Denominator">The time signature at tick 0's note value.</param>
/// <param name="Parts">The parts, from <see cref="NewChartSettings.Instruments"/>.</param>
/// <param name="Drums">How the drums are charted, when <paramref name="Parts"/> has drums.</param>
public sealed record ChartSettingsValues(
    int Resolution,
    long MicrosecondsPerQuarter,
    int Numerator,
    int Denominator,
    IReadOnlySet<ChartInstrument> Parts,
    DrumsVariant Drums)
{
    /// <summary>
    /// What <paramref name="chart"/> has: the last tempo and time signature at tick 0, as the games read them (120 BPM
    /// and 4/4 when there's none), its parts that Chart Builder edits, and its drum variant, Pro when it has none, as the
    /// games read drums that nothing says otherwise of.
    /// </summary>
    public static ChartSettingsValues Of(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var map = chart.TempoMap;
        var tempos = map.Tempos.At(0);
        var signatures = map.TimeSignatures.At(0);
        return new ChartSettingsValues(
            chart.Resolution,
            tempos.IsEmpty ? TempoMap.DefaultMicrosecondsPerQuarter : tempos[^1].MicrosecondsPerQuarter,
            signatures.IsEmpty ? 4 : signatures[^1].Numerator,
            signatures.IsEmpty ? 4 : signatures[^1].Denominator,
            chart.Parts.Select(static p => p.Instrument).Where(NewChartSettings.Instruments.Contains).ToHashSet(),
            chart.DrumsVariant ?? DrumsVariant.Pro);
    }
}

/// <summary>A change in Chart settings on an open song (charter-spec C1, Applying), as one undoable command.</summary>
public static class ChartSettingsEdit
{
    /// <summary>
    /// The command that gives <paramref name="chart"/> <paramref name="values"/>, or null when it has them already, so
    /// a change that leaves the chart as it was adds no undo step. In order: the resolution, which keeps every time; the
    /// starting tempo and time signature, keeping ticks or times as <paramref name="keepTimes"/> says; then the parts
    /// removed, the drum variant and the parts added.
    /// </summary>
    /// <remarks>
    /// The drum variant of drums with notes can't be changed until their conversion comes (CM3); the command throws
    /// when it's applied. Parts that aren't Chart Builder's are left as they are.
    /// </remarks>
    public static IChartCommand? CommandFor(EditableChart chart, ChartSettingsValues values, bool keepTimes)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(values);
        var now = ChartSettingsValues.Of(chart);
        var commands = new List<IChartCommand>();

        if (values.Resolution != now.Resolution)
            commands.Add(new ChangeResolution(values.Resolution));

        // A chart with no marker at tick 0 is read as 120 BPM and 4/4, so values that say just that add none.
        bool tempo = values.MicrosecondsPerQuarter != now.MicrosecondsPerQuarter;
        bool signature = values.Numerator != now.Numerator || values.Denominator != now.Denominator;
        if (tempo || signature)
        {
            commands.Add(new SetStartingTempo(
                tempo ? values.MicrosecondsPerQuarter : null,
                signature ? values.Numerator : null,
                signature ? values.Denominator : null,
                keepTimes));
        }

        foreach (var instrument in now.Parts.Where(p => !values.Parts.Contains(p)).Order())
            commands.Add(new RemovePart(instrument));

        // Drums added take the variant from AddPart when the song has none; one the song has is changed first.
        if (values.Parts.Contains(ChartInstrument.Drums) && chart.DrumsVariant is { } variant && variant != values.Drums)
            commands.Add(new SetDrumsVariant(values.Drums));

        foreach (var instrument in values.Parts.Where(p => !now.Parts.Contains(p)).Order())
            commands.Add(instrument == ChartInstrument.Drums ? new AddPart(instrument, values.Drums) : new AddPart(instrument));

        return commands.Count == 0 ? null : new CompositeCommand(commands);
    }
}
