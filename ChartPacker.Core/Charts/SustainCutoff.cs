namespace ChartPacker.Core.Charts;

/// <summary>
/// How long a MIDI note has to be held to be a sustain, and keeping notes clear of it.
/// </summary>
/// <remarks>
/// YARG reads a note held for less than the cutoff as a note without a sustain; another game may
/// take "no longer than" instead. So a chart this writes keeps every note clear of the cutoff:
/// a note without a sustain shorter than it, a sustain longer.
/// </remarks>
internal static class SustainCutoff
{
    /// <summary>The shortest a sustain is written: longer than a cutoff of 2, which a one-tick note is shorter than.</summary>
    public const long ShortestSustain = 3;

    /// <summary>
    /// The cutoff a MIDI chart of <paramref name="resolution"/> is read with: song.ini's
    /// <paramref name="setting"/>, where 0 is taken as 1, or a third of a beat.
    /// </summary>
    public static long Of(int resolution, long? setting) => setting is { } ticks ? Math.Max(1, ticks) : resolution / 3;

    /// <summary>
    /// The cutoff to write for a chart of <paramref name="resolution"/> whose shortest sustain is
    /// <paramref name="shortest"/>: <see langword="null"/> for the default when every sustain is longer
    /// than it, otherwise a tick shorter than the shortest, and no less than <see cref="ShortestSustain"/> - 1.
    /// </summary>
    public static long? For(int resolution, long? shortest) =>
        shortest is { } ticks && ticks <= resolution / 3 ? Math.Max(ShortestSustain - 1, ticks - 1) : null;

    /// <summary>The pitches of a fret track's gems, every difficulty's; its open notes only with <c>ENHANCED_OPENS</c>.</summary>
    public static HashSet<int> GemPitches(ChartInstrumentKind kind, bool enhancedOpens)
    {
        var pitches = new HashSet<int>();
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            int b = MidiLayout.BaseOf(kind, difficulty);
            int low = kind == ChartInstrumentKind.FiveFret && enhancedOpens ? b - 1 : b;
            int high = kind == ChartInstrumentKind.SixFret ? b + 6 : b + 4;
            for (int pitch = low; pitch <= high; pitch++)
                pitches.Add(pitch);
        }

        return pitches;
    }

    /// <summary>A held note of a track: the indexes of its note-on and note-off among the track's messages.</summary>
    public readonly record struct Held(int Pitch, int On, int Off);

    /// <summary>
    /// The notes at <paramref name="pitches"/> among <paramref name="messages"/>, which are in tick order,
    /// paired first on, first off as <see cref="MidiChartReader"/> pairs them; a note never let go is left out.
    /// </summary>
    public static List<Held> NotesOf(IReadOnlyList<MidiMessage> messages, IReadOnlySet<int> pitches)
    {
        var notes = new List<Held>();
        var held = new Dictionary<int, Queue<int>>();
        for (int i = 0; i < messages.Count; i++)
        {
            byte[] bytes = messages[i].Bytes;
            if (bytes.Length < 3 || (bytes[0] & 0xF0) is not (0x80 or 0x90) || !pitches.Contains(bytes[1]))
                continue;

            if (!held.TryGetValue(bytes[1], out var starts))
                held[bytes[1]] = starts = new Queue<int>();
            if ((bytes[0] & 0xF0) == 0x90 && bytes[2] > 0)
                starts.Enqueue(i);
            else if (starts.TryDequeue(out int on))
                notes.Add(new Held(bytes[1], on, i));
        }

        return notes;
    }

    /// <summary>
    /// Moves the note-offs of <paramref name="notes"/> among <paramref name="messages"/> so that each reads as
    /// <paramref name="isSustain"/> says under <paramref name="cutoff"/>, with room either way: a note held for
    /// the cutoff or longer is let go a tick after it starts, and a sustain no longer than the cutoff is held
    /// a tick past it, though never into the next note of its pitch. Notes already clear of it are left alone.
    /// </summary>
    public static void Normalize(List<MidiMessage> messages, IReadOnlyList<Held> notes, Func<Held, bool> isSustain, long cutoff)
    {
        foreach (var byPitch in notes.GroupBy(n => n.Pitch))
        {
            var list = byPitch.OrderBy(n => messages[n.On].Tick).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var note = list[i];
                long start = messages[note.On].Tick;
                long end = messages[note.Off].Tick;
                long next = i + 1 < list.Count ? messages[list[i + 1].On].Tick : long.MaxValue;

                long want = end;
                if (!isSustain(note) && end - start >= cutoff)
                    want = start + 1;
                else if (isSustain(note) && end - start <= cutoff)
                    want = Math.Min(start + cutoff + 1, next);
                if (want != end)
                    messages[note.Off] = messages[note.Off] with { Tick = want };
            }
        }
    }
}
