using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// Measures how hard a five-fret track is to play cleanly, moment by moment and as a whole (docs/difficulty-model.md):
/// strum, note, rhythm and pattern strain, built up by each note's cost and decaying between them, and endurance, which
/// builds while the four are high.
/// </summary>
/// <remarks>
/// <para>The strain model is adapted, as an idea, from the one osu!'s star rating uses; no code is taken from it.</para>
/// <para>Each pass reads the notes from the start, in time order. A track of a few thousand notes takes milliseconds, so
/// after an edit the whole track is read again rather than only from the edit on (§8.2 leaves that to the
/// implementation). Pure and thread-safe.</para>
/// </remarks>
public static class DifficultyAnalyzer
{
    /// <summary>The difficulty of <paramref name="notes"/>, in time order, over the whole chart.</summary>
    public static DifficultyProfile Analyze(IReadOnlyList<DifficultyNote> notes, DifficultyParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var model = DifficultyModel.For(parameters ?? DifficultyParameters.Default);
        double end = notes.Count == 0 ? 0 : notes[^1].TimeMs + model.Parameters.WindowMs;
        return Run(model, notes, end);
    }

    /// <summary>
    /// The difficulty of the notes of <paramref name="notes"/> within <paramref name="isolate"/>, as if the stretch were
    /// played alone (§6.4): nothing built up before it, and its first note with none before. Notes keep the types the whole
    /// chart gave them.
    /// </summary>
    public static DifficultyProfile Analyze(IReadOnlyList<DifficultyNote> notes, DifficultySpan isolate, DifficultyParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var model = DifficultyModel.For(parameters ?? DifficultyParameters.Default);
        var within = notes.Where(n => n.TimeMs >= isolate.FromMs && n.TimeMs < isolate.ToMs).ToList();
        return Run(model, within, isolate.ToMs);
    }

    /// <summary>The difficulty of <paramref name="track"/> as the games read it (see <see cref="DifficultyNotes.Of"/>).</summary>
    public static DifficultyProfile Analyze(NoteTrack track, TempoMap tempo, long? endTick = null, DifficultyParameters? parameters = null) =>
        Analyze(DifficultyNotes.Of(track, tempo, endTick, parameters), parameters);

    // One pass: a sample just after each note, and at the start of each window from the first note to endMs.
    private static DifficultyProfile Run(DifficultyModel model, IReadOnlyList<DifficultyNote> notes, double endMs)
    {
        var state = new DifficultyState(model);
        var samples = new List<DifficultySample>((notes.Count * 2) + 16);
        double window = model.Parameters.WindowMs;
        long next = 0;
        foreach (var note in notes)
        {
            if (state.Count == 0)
                next = (long)Math.Ceiling(note.TimeMs / window);
            for (; next * window <= note.TimeMs; next++)
                samples.Add(SampleAt(model, state, next * window));
            state.Advance(note);
            samples.Add(new DifficultySample(note.TimeMs, state.Levels, model.Combine(state.Levels)));
        }

        if (state.Count > 0)
        {
            for (; next * window < endMs; next++)
                samples.Add(SampleAt(model, state, next * window));
        }

        // The whole chart is scored from the window its first note is in: silence before it isn't easy playing.
        double startMs = notes.Count == 0 ? 0 : Math.Floor(notes[0].TimeMs / window) * window;
        return new DifficultyProfile(model, [.. samples], startMs, endMs);
    }

    private static DifficultySample SampleAt(DifficultyModel model, DifficultyState state, double timeMs)
    {
        state.AdvanceTo(timeMs);
        return new DifficultySample(timeMs, state.Levels, model.Combine(state.Levels));
    }
}
