using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Merging;

/// <summary>Writes a chart's vocals on their own, as a <c>notes.mid</c> another song can take its lyrics from.</summary>
/// <remarks>
/// The file has the chart's tempo map and its vocal tracks, <c>PART VOCALS</c> and the harmonies,
/// with each track's events as the chart has them. A <c>.chart</c> is converted first, so its
/// vocals are lyrics and phrases only. A MIDI file on its own has no <c>song.ini</c> to give it a
/// <c>delay</c>, so the chart's delay and the offset are worked into the ticks: the file's first
/// tick is the start of the song.
/// </remarks>
public static class LyricsExport
{
    /// <summary>The vocals of <paramref name="chart"/>, sung <paramref name="offset"/> later, as a MIDI file.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="chartName">Its stored name, <c>notes.chart</c> or <c>notes.mid</c>.</param>
    /// <param name="metadata">The song's metadata, for its <c>delay</c>.</param>
    /// <param name="offset">How much later than the chart places them the vocals are sung.</param>
    /// <exception cref="ChartFormatException">
    /// The chart is malformed or has no vocals, or they'd start before the song does.
    /// </exception>
    public static byte[] ToMidi(byte[] chart, string chartName, IReadOnlyList<KeyValuePair<string, string>> metadata, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(chartName);
        ArgumentNullException.ThrowIfNull(metadata);

        var (loaded, _) = MidiChart.Load(chart, chartName, metadata);
        byte[] midi = loaded.Midi;
        var (_, division, chunks) = MidiFile.ReadChunks(midi);
        var tracks = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(midi, c)).ToList();
        var tempo = MidiChartReader.TempoOf(midi, division, tracks);

        var vocals = VocalReader.Read(midi, ChartFiles.MidiName, tempo);
        TimeSpan delay = loaded.Delay + offset;
        if (VocalsEditor.StartOf(vocals, delay, TimeSpan.Zero) is not { } start)
            throw new ChartFormatException("The chart has no vocals.");
        if (start < TimeSpan.Zero)
            throw new ChartFormatException($"The vocals start {Math.Ceiling(-start.TotalMilliseconds):0} ms before the song does.");

        var map = MidiMerger.MapOf(tempo, delay, tempo, TimeSpan.Zero);
        var placed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var events in tracks)
        {
            if (MidiFile.NameOf(midi, events) is { } name && VocalReader.IsVocalTrack(name) && !placed.ContainsKey(name))
                placed[name] = MidiWriter.Track(MidiMerger.Place(midi, events, map));
        }

        return MidiWriter.WriteFile(division, [MidiWriter.Track(ChartToMidi.Conductor(tempo)), .. VocalReader.TrackNames.Where(placed.ContainsKey).Select(n => placed[n])]);
    }
}
