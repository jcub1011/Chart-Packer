using System.Globalization;
using System.Text;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Merging;

/// <summary>A chart with its vocals replaced or removed.</summary>
/// <param name="Bytes">The changed chart.</param>
/// <param name="LyricsOnly">
/// The chart is a <c>.chart</c>, which keeps lyrics and phrases only, and the new vocals had sung
/// notes or harmonies that were left out.
/// </param>
public sealed record VocalsEditResult(byte[] Bytes, bool LyricsOnly);

/// <summary>
/// Replaces a chart's vocals, <c>PART VOCALS</c> and the harmonies together, with another chart's,
/// removes them, or moves them in time; every other part of the chart stays as it was.
/// </summary>
/// <remarks>
/// <para>The new vocals are placed by song time, as <see cref="MidiMerger"/> places a part: each event
/// goes to the tick that plays at the same moment against the audio, so they line up whatever the
/// two charts' tempo maps and delays.</para>
/// <para>In a MIDI file the vocal tracks are dropped and the new ones added after the others, whose
/// bytes are copied as they are. In a <c>.chart</c> only the lyric and phrase events of <c>[Events]</c>
/// change.</para>
/// </remarks>
public static class VocalsEditor
{
    /// <summary>Gives <paramref name="target"/> the vocals of <paramref name="source"/>, or none when it's <see langword="null"/>.</summary>
    /// <param name="target">The chart to change.</param>
    /// <param name="targetName">Its stored name, <c>notes.chart</c> or <c>notes.mid</c>.</param>
    /// <param name="targetMetadata">The song's metadata, for its <c>delay</c>.</param>
    /// <param name="source">The chart to take the vocals from.</param>
    /// <param name="offset">How much later than in <paramref name="source"/> the vocals are sung.</param>
    /// <exception cref="ArgumentException"><paramref name="targetName"/> isn't a chart's name.</exception>
    /// <exception cref="ChartFormatException">
    /// A chart is malformed, <paramref name="offset"/> would move the vocals before the start of the
    /// song, or the result didn't check out.
    /// </exception>
    public static VocalsEditResult Apply(byte[] target, string targetName, IReadOnlyList<KeyValuePair<string, string>> targetMetadata, MidiChart? source, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(targetMetadata);

        return Edit(target, targetName, targetMetadata, offset, _ => source is null
            ? null
            : new Placement(source.Midi, VocalReader.Read(source.Midi, ChartFiles.MidiName), source.Delay + offset));
    }

    /// <summary>Moves the vocals <paramref name="target"/> has by <paramref name="offset"/> of song time, later when positive.</summary>
    /// <remarks>Each event moves to the nearest tick, so where the tempo changes a note keeps its length in time.</remarks>
    /// <param name="targetName">Its stored name, <c>notes.chart</c> or <c>notes.mid</c>.</param>
    /// <param name="targetMetadata">The song's metadata, for its <c>delay</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="targetName"/> isn't a chart's name.</exception>
    /// <exception cref="ChartFormatException">
    /// The chart is malformed, <paramref name="offset"/> would move the vocals before the start of the
    /// song, or the result didn't check out.
    /// </exception>
    public static VocalsEditResult Shift(byte[] target, string targetName, IReadOnlyList<KeyValuePair<string, string>> targetMetadata, TimeSpan offset)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetName);
        ArgumentNullException.ThrowIfNull(targetMetadata);

        var vocals = VocalReader.Read(target, targetName);
        if (offset == TimeSpan.Zero || !vocals.HasVocals)
            return new VocalsEditResult(target, false);

        // The chart's own vocals, placed as though its delay were later by the offset.
        return Edit(target, targetName, targetMetadata, offset, delay => new Placement(ChartReader.IsMidi(targetName) ? target : null, vocals, delay + offset));
    }

    /// <summary>Gives <paramref name="target"/> the vocals <paramref name="placementOf"/> makes from its delay; none when it makes none.</summary>
    private static VocalsEditResult Edit(
        byte[] target, string targetName, IReadOnlyList<KeyValuePair<string, string>> targetMetadata, TimeSpan offset, Func<TimeSpan, Placement?> placementOf)
    {
        var options = ChartReadOptions.From(targetMetadata);
        var before = ChartReader.Read(target, targetName, options);
        var delay = SongChartSettings.DelayOf(targetMetadata, before);
        var placement = placementOf(delay);
        if (placement is not null && offset < TimeSpan.Zero)
            EnsureInSong(placement, delay, offset);

        var result = ChartReader.IsMidi(targetName)
            ? new VocalsEditResult(ApplyMidi(target, placement, delay), false)
            : ApplyText(target, before.Tempo, placement, delay);

        Check(before, ChartReader.Read(result.Bytes, targetName, options));
        return result;
    }

    /// <summary>
    /// Throws when moving the vocals earlier by <paramref name="offset"/> would put their first event
    /// before the start of the song, where it would land on the first tick with whatever else did.
    /// </summary>
    private static void EnsureInSong(Placement placement, TimeSpan delay, TimeSpan offset)
    {
        var ticks = placement.Vocals.Parts
            .SelectMany(p => p.Notes.Select(n => n.Tick).Concat(p.Lyrics.Select(l => l.Tick)).Concat(p.Phrases.Select(f => f.Tick)))
            .ToList();
        if (ticks.Count > 0 && placement.Vocals.Tempo.SecondsAt(ticks.Min()) + (placement.Delay - delay).TotalSeconds < 0)
            throw new ChartFormatException($"Moving the vocals {-offset.TotalMilliseconds:0} ms earlier would put them before the start of the song.");
    }

    /// <summary>Throws when anything but the vocals changed; they aren't read as instruments, so the instruments must read the same.</summary>
    private static void Check(ChartDocument before, ChartDocument after)
    {
        if (!before.Tempo.SameAs(after.Tempo)
            || !before.Tracks.Keys.ToHashSet().SetEquals(after.Tracks.Keys)
            || before.Tracks.Any(p => !p.Value.Notes.SequenceEqual(after.Tracks[p.Key].Notes)))
            throw new ChartFormatException("Changing the vocals changed more of the chart than the vocals.");
    }

    private static byte[] ApplyMidi(byte[] target, Placement? placement, TimeSpan delay)
    {
        var (_, division, chunks) = MidiFile.ReadChunks(target);
        var tracks = chunks.Where(c => c.Id == "MTrk").Select(c => (Chunk: c, Events: MidiFile.ReadEvents(target, c))).ToList();
        var tempo = MidiChartReader.TempoOf(target, division, [.. tracks.Select(t => t.Events)]);

        var kept = tracks.Where(t => !VocalReader.IsVocalTrack(MidiFile.NameOf(target, t.Events))).ToList();
        if (!MidiChartReader.TempoOf(target, division, [.. kept.Select(t => t.Events)]).SameAs(tempo))
            throw new ChartFormatException("The chart keeps tempo changes in its vocal tracks, so they can't be changed.");

        var trackChunks = kept.Select(t => target[t.Chunk.Start..t.Chunk.End]).ToList();
        if (placement is not null)
            trackChunks.AddRange(VocalTracksOf(placement.Midi!, placement.Delay, tempo, delay));
        return MidiWriter.WriteFile(division, trackChunks);
    }

    /// <summary>
    /// The first track of each vocal track name in <paramref name="midi"/>, which plays <paramref name="fromDelay"/>
    /// later than its ticks say, placed against <paramref name="tempo"/> and <paramref name="delay"/>.
    /// </summary>
    private static IEnumerable<byte[]> VocalTracksOf(byte[] midi, TimeSpan fromDelay, TempoMap tempo, TimeSpan delay)
    {
        var (_, division, chunks) = MidiFile.ReadChunks(midi);
        var tracks = chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.ReadEvents(midi, c)).ToList();
        var map = MidiMerger.MapOf(MidiChartReader.TempoOf(midi, division, tracks), fromDelay, tempo, delay);

        var placed = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var events in tracks)
        {
            if (MidiFile.NameOf(midi, events) is { } name && VocalReader.IsVocalTrack(name) && !placed.ContainsKey(name))
                placed[name] = MidiWriter.Track(MidiMerger.Place(midi, events, map));
        }

        return VocalReader.TrackNames.Where(placed.ContainsKey).Select(n => placed[n]);
    }

    private static VocalsEditResult ApplyText(byte[] target, TempoMap tempo, Placement? placement, TimeSpan delay)
    {
        if (target is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            throw new ChartFormatException("A UTF-16 .chart can't be changed.");

        List<(long Tick, int Order, string Text)> added = [];
        bool lyricsOnly = false;
        if (placement is not null)
        {
            var vocals = placement.Vocals;
            if (vocals.Main is { } main)
            {
                var map = MidiMerger.MapOf(vocals.Tempo, placement.Delay, tempo, delay);
                lyricsOnly = main.Notes.Count > 0 || vocals.Parts.Count > 1;

                // At a tick, a phrase ends before the next starts, and starts before its first lyric.
                foreach (var phrase in main.Phrases)
                {
                    added.Add((map(phrase.Tick + phrase.Length), 0, "phrase_end"));
                    added.Add((map(phrase.Tick), 1, "phrase_start"));
                }

                foreach (var lyric in main.Lyrics)
                {
                    string text = lyric.Text.Replace("\"", string.Empty, StringComparison.Ordinal).ReplaceLineEndings(" ").Trim();
                    if (text.Length > 0)
                        added.Add((map(lyric.Tick), 2, "lyric " + text));
                }
            }
        }

        return new VocalsEditResult(RewriteEvents(target, added), lyricsOnly);
    }

    /// <summary>
    /// <paramref name="target"/> with the lyric and phrase lines of its <c>[Events]</c> replaced by
    /// <paramref name="added"/>, each sorted in among the other events by its tick. Every other line
    /// is copied as it is. A chart without <c>[Events]</c> gets one at the end, when there's something to add.
    /// </summary>
    private static byte[] RewriteEvents(byte[] target, List<(long Tick, int Order, string Text)> added)
    {
        string newline = Array.IndexOf(target, (byte)'\n') is > 0 and var first && target[first - 1] == '\r' ? "\r\n" : "\n";
        using var output = new MemoryStream(target.Length + (added.Count * 32));
        string? section = null;
        bool inEvents = false, sawEvents = false;
        string indent = "  ";
        var body = new List<(long Tick, int Order, byte[] Line)>();

        int pos = 0;
        while (pos < target.Length)
        {
            int end = Array.IndexOf(target, (byte)'\n', pos);
            end = end < 0 ? target.Length : end + 1;
            byte[] line = target[pos..end];
            string content = Encoding.UTF8.GetString(line).Trim();
            pos = end;

            if (inEvents)
            {
                if (content == "}")
                {
                    WriteEvents(output, body, added, indent, newline);
                    inEvents = false;
                    output.Write(line);
                    continue;
                }

                if (body.Count == 0 && content.Length > 0)
                {
                    string raw = Encoding.UTF8.GetString(line);
                    indent = raw[..(raw.Length - raw.TrimStart().Length)];
                }

                if (VocalReader.TextEvent(content) is var (tick, text))
                {
                    if (!VocalReader.IsChartVocalEvent(text))
                        body.Add((tick, -1, line));
                }
                else
                {
                    // Not an event: it stays with the line before it.
                    body.Add((body.Count > 0 ? body[^1].Tick : 0, -1, line));
                }

                continue;
            }

            output.Write(line);
            if (content.StartsWith('[') && content.EndsWith(']'))
                section = content[1..^1].Trim();
            else if (content == "{" && section is not null && section.Equals("Events", StringComparison.OrdinalIgnoreCase) && !sawEvents)
                (inEvents, sawEvents) = (true, true);
        }

        if (inEvents)
            throw new ChartFormatException("The section [Events] isn't closed.");

        if (!sawEvents && added.Count > 0)
        {
            if (output.Length > 0 && target[^1] != '\n')
                output.Write(Encoding.UTF8.GetBytes(newline));
            output.Write(Encoding.UTF8.GetBytes($"[Events]{newline}{{{newline}"));
            WriteEvents(output, body, added, indent, newline);
            output.Write(Encoding.UTF8.GetBytes($"}}{newline}"));
        }

        return output.ToArray();
    }

    private static void WriteEvents(MemoryStream output, List<(long Tick, int Order, byte[] Line)> body, List<(long Tick, int Order, string Text)> added, string indent, string newline)
    {
        var lines = body.Concat(added.Select(a => (a.Tick, a.Order, Encoding.UTF8.GetBytes($"{indent}{a.Tick.ToString(CultureInfo.InvariantCulture)} = E \"{a.Text}\"{newline}"))));

        // A stable sort keeps the chart's own events in their order.
        foreach (var (_, _, line) in lines.OrderBy(l => l.Tick).ThenBy(l => l.Order))
            output.Write(line);
        body.Clear();
    }

    /// <summary>Vocals to give a chart, and what places them against its audio.</summary>
    /// <param name="Midi">The MIDI file they're in, for a MIDI chart; <see langword="null"/> when they're a <c>.chart</c>'s own.</param>
    /// <param name="Delay">How much later than their ticks say they're sung.</param>
    private sealed record Placement(byte[]? Midi, VocalChart Vocals, TimeSpan Delay);
}
