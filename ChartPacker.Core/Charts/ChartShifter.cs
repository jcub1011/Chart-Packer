using System.Globalization;
using System.Text;

namespace ChartPacker.Core.Charts;

/// <summary>A chart with some of its tracks moved in time.</summary>
/// <param name="Bytes">The changed file; the original's bytes when nothing moved.</param>
/// <param name="Document">The changed file as read back.</param>
/// <param name="ChangedMarkings">
/// Moved tracks where some notes changed what they are (a tap, a cymbal, in star power, a HOPO)
/// because a marker the difficulties share stayed where it was.
/// </param>
public sealed record ChartShiftResult(byte[] Bytes, ChartDocument Document, IReadOnlyList<ChartTrackKey> ChangedMarkings);

/// <summary>
/// Moves the notes of single instrument difficulties of a chart by an amount of song time, and
/// leaves every other byte of the file as it was.
/// </summary>
/// <remarks>
/// <para>Each event is moved by the same song time, rounded to the nearest tick, so where the tempo
/// changes a sustain keeps its length in time rather than in ticks. Nothing moves before the start.</para>
/// <para>A <c>.chart</c> keeps each difficulty in its own section, star power included, so the
/// whole section moves. A MIDI track keeps its difficulties apart only by pitch: the notes, forcing
/// markers and Phase Shift sysex events of the difficulty move, and the markers every difficulty
/// shares (star power, solos, taps, tom markers) stay put; <see cref="ChartShiftResult.ChangedMarkings"/>
/// says when that changed a note.</para>
/// </remarks>
public static class ChartShifter
{
    /// <summary>
    /// Moves each track of <paramref name="offsetsMs"/> by its milliseconds, later when positive,
    /// then reads the result back to check that only those tracks moved and none lost a note.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="storedName"/> isn't a chart's name.</exception>
    /// <exception cref="ChartFormatException">The chart is malformed, or the result didn't check out.</exception>
    public static ChartShiftResult Shift(byte[] bytes, string storedName, IReadOnlyDictionary<ChartTrackKey, int> offsetsMs, ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(offsetsMs);

        var before = ChartReader.Read(bytes, storedName, options);
        var offsets = offsetsMs
            .Where(p => p.Value != 0 && before.Tracks.ContainsKey(p.Key))
            .ToDictionary(p => p.Key, p => TimeSpan.FromMilliseconds(p.Value));
        if (offsets.Count == 0)
            return new ChartShiftResult(bytes, before, []);

        foreach (var (key, offset) in offsets)
        {
            if (before.Tempo.SecondsAt(before.Tracks[key].Notes[0].Tick) + offset.TotalSeconds < 0)
                throw new ChartFormatException($"Moving {key.Difficulty} {key.Instrument} {-offset.TotalMilliseconds:0} ms earlier would put its first note before the start of the song.");
        }

        byte[] shifted = ChartReader.IsMidi(storedName) ? ShiftMidi(bytes, before, offsets) : ShiftText(bytes, before.Tempo, offsets);
        var after = ChartReader.Read(shifted, storedName, options);
        return new ChartShiftResult(shifted, after, Check(before, after, offsets));
    }

    /// <summary>The moved tracks whose notes changed markings; throws when anything else changed.</summary>
    private static List<ChartTrackKey> Check(ChartDocument before, ChartDocument after, Dictionary<ChartTrackKey, TimeSpan> offsets)
    {
        if (!before.Tempo.SameAs(after.Tempo) || !before.Tracks.Keys.ToHashSet().SetEquals(after.Tracks.Keys))
            throw new ChartFormatException("Moving the notes changed more of the chart than those notes.");

        var changed = new List<ChartTrackKey>();
        foreach (var (key, track) in before.Tracks)
        {
            var notes = track.Notes;
            var moved = after.Tracks[key].Notes;
            if (!offsets.ContainsKey(key))
            {
                if (!notes.SequenceEqual(moved))
                    throw new ChartFormatException($"Moving the notes changed {key.Difficulty} {key.Instrument}, which wasn't moved.");
                continue;
            }

            if (notes.Count != moved.Count)
                throw new ChartFormatException($"Moving {key.Difficulty} {key.Instrument} would merge notes at the start of the song.");

            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i].Lane != moved[i].Lane)
                    throw new ChartFormatException($"Moving {key.Difficulty} {key.Instrument} reordered its notes.");
                if (notes[i].Flags != moved[i].Flags)
                {
                    changed.Add(key);
                    break;
                }
            }
        }

        return changed;
    }

    private static byte[] ShiftText(byte[] bytes, TempoMap tempo, Dictionary<ChartTrackKey, TimeSpan> offsets)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
            throw new ChartFormatException("A UTF-16 .chart can't be changed.");

        var bySection = offsets.ToDictionary(p => ChartTextReader.SectionName(p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        using var output = new MemoryStream(bytes.Length + 4096);
        string? section = null;
        TimeSpan? offset = null;
        var body = new List<(long Tick, byte[] Line)>();

        int pos = 0;
        while (pos < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', pos);
            end = end < 0 ? bytes.Length : end + 1;
            var line = bytes.AsSpan(pos, end - pos);
            string content = Encoding.Latin1.GetString(line).Trim();
            pos = end;

            if (offset is { } by)
            {
                if (content == "}")
                {
                    // Every line moves the same way, so a stable sort only settles lines that met at a tick.
                    foreach (var (_, moved) in body.OrderBy(l => l.Tick))
                        output.Write(moved);
                    body.Clear();
                    offset = null;
                    output.Write(line);
                }
                else
                {
                    body.Add(ShiftLine(line.ToArray(), tempo, by, body.Count > 0 ? body[^1].Tick : 0));
                }

                continue;
            }

            output.Write(line);
            if (content.StartsWith('[') && content.EndsWith(']'))
                section = content[1..^1].Trim();
            else if (content == "{" && section is not null && bySection.TryGetValue(section, out var sectionOffset))
                offset = sectionOffset;
        }

        if (body.Count > 0)
            throw new ChartFormatException($"The section [{section}] isn't closed.");
        return output.ToArray();
    }

    /// <summary>
    /// <paramref name="line"/>, <c>tick = TYPE ...</c>, with its tick moved; for a note or phrase
    /// (<c>N</c>, <c>S</c>) its length too, so that its end moves by the same time. A line that isn't
    /// an event stays as it is, sorted with the line before it.
    /// </summary>
    private static (long Tick, byte[] Line) ShiftLine(byte[] line, TempoMap tempo, TimeSpan offset, long previousTick)
    {
        int i = SkipSpace(line, 0);
        var (tickStart, tickEnd) = (i, SkipDigits(line, i));
        if (tickEnd == tickStart || !TryNumber(line, tickStart, tickEnd, out long tick))
            return (previousTick, line);

        i = SkipSpace(line, tickEnd);
        if (i >= line.Length || line[i] != '=')
            return (previousTick, line);

        i = SkipSpace(line, i + 1);
        int typeStart = i;
        while (i < line.Length && line[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            i++;
        string type = Encoding.ASCII.GetString(line, typeStart, i - typeStart);

        long newTick = tempo.Shift(tick, offset);
        using var result = new MemoryStream(line.Length + 8);
        result.Write(line, 0, tickStart);
        result.Write(Encoding.ASCII.GetBytes(newTick.ToString(CultureInfo.InvariantCulture)));

        if (type is "N" or "S")
        {
            i = SkipDigits(line, SkipSpace(line, i));
            var (lengthStart, lengthEnd) = (SkipSpace(line, i), SkipDigits(line, SkipSpace(line, i)));
            if (lengthEnd > lengthStart && TryNumber(line, lengthStart, lengthEnd, out long length))
            {
                long newLength = Math.Max(0, tempo.Shift(tick + length, offset) - newTick);
                result.Write(line, tickEnd, lengthStart - tickEnd);
                result.Write(Encoding.ASCII.GetBytes(newLength.ToString(CultureInfo.InvariantCulture)));
                result.Write(line, lengthEnd, line.Length - lengthEnd);
                return (newTick, result.ToArray());
            }
        }

        result.Write(line, tickEnd, line.Length - tickEnd);
        return (newTick, result.ToArray());
    }

    private static int SkipSpace(byte[] line, int i)
    {
        while (i < line.Length && line[i] is (byte)' ' or (byte)'\t')
            i++;
        return i;
    }

    private static int SkipDigits(byte[] line, int i)
    {
        while (i < line.Length && line[i] is >= (byte)'0' and <= (byte)'9')
            i++;
        return i;
    }

    private static bool TryNumber(byte[] line, int start, int end, out long value) =>
        long.TryParse(line.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static byte[] ShiftMidi(byte[] bytes, ChartDocument before, Dictionary<ChartTrackKey, TimeSpan> offsets)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(bytes);
        using var output = new MemoryStream(bytes.Length + 4096);
        int copied = 0;
        var done = new HashSet<ChartInstrument>();

        foreach (var chunk in chunks.Where(c => c.Id == "MTrk"))
        {
            var events = MidiFile.ReadEvents(bytes, chunk);
            if (MidiChartReader.NameOf(bytes, events) is not { } name || !MidiLayout.TrackNames.TryGetValue(name, out var instrument) || !done.Add(instrument))
                continue;

            var byDifficulty = offsets.Where(p => p.Key.Instrument == instrument).ToDictionary(p => p.Key.Difficulty, p => p.Value);
            if (byDifficulty.Count == 0)
                continue;

            // Every difficulty moving together moves the whole track, its shared markers too.
            var difficulties = before.DifficultiesOf(instrument);
            TimeSpan? whole = difficulties.All(byDifficulty.ContainsKey) && byDifficulty.Values.Distinct().Count() == 1 ? byDifficulty.Values.First() : null;

            output.Write(bytes, copied, chunk.Start - copied);
            WriteTrack(output, bytes, events, before.Tempo, ChartInstruments.KindOf(instrument), byDifficulty, whole);
            copied = chunk.End;
        }

        output.Write(bytes, copied, bytes.Length - copied);
        return output.ToArray();
    }

    /// <summary>
    /// Writes the track <paramref name="events"/> with the difficulties' events moved, or, given
    /// <paramref name="whole"/>, every event but the track's name. It's written without running status.
    /// </summary>
    private static void WriteTrack(
        MemoryStream output, byte[] bytes, List<MidiEvent> events, TempoMap tempo, ChartInstrumentKind kind, Dictionary<ChartDifficulty, TimeSpan> offsets, TimeSpan? whole)
    {
        TimeSpan? OffsetOf(MidiEvent e)
        {
            if (whole is { } all)
                return e.IsMeta && bytes[e.DataStart] == 0x03 ? null : all;

            ChartDifficulty? difficulty = e.Kind is 0x80 or 0x90 ? MidiLayout.DifficultyOf(kind, bytes[e.DataStart])
                : e.IsSysex ? MidiLayout.DifficultyOfSysex(MidiFile.SysexOf(bytes, e))
                : null;
            return difficulty is { } d && offsets.TryGetValue(d, out var offset) ? offset : null;
        }

        var endOfTrack = events.Where(e => e.IsMeta && bytes[e.DataStart] == MidiFile.EndOfTrack).Cast<MidiEvent?>().LastOrDefault();
        var placed = events
            .Where(e => !(e.IsMeta && bytes[e.DataStart] == MidiFile.EndOfTrack))
            .Select(e => OffsetOf(e) is { } offset ? e with { Tick = tempo.Shift(e.Tick, offset) } : e)
            .OrderBy(e => e.Tick)
            .ToList();

        // The end of the track stays last, however far the notes moved.
        if (endOfTrack is { } eot)
            placed.Add(eot with { Tick = Math.Max(eot.Tick, placed.Count > 0 ? placed[^1].Tick : 0) });

        MidiWriter.WriteTrack(output, bytes, placed);
    }
}
