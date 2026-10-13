using ChartPacker.Core.Input;

namespace ChartPacker.Core.Charting.Playtest;

/// <summary>A problem the playtest found (charter-spec C19 item 4), at a tick in the game view's lane, with how many passes had it.</summary>
/// <param name="Lane">The game view lane: a note's first lane, an overhit's pad; −2 for an overstrum.</param>
/// <param name="Count">How many times it happened, over every pass.</param>
/// <param name="OffsetMs">For a dropped sustain, how early it was let go, the last time; 0 otherwise.</param>
public sealed record PlaytestProblem(long Tick, int Lane, JudgeKind Kind, int Count, double OffsetMs);

/// <summary>How a note was played the last time it was judged.</summary>
public readonly record struct NoteMark(JudgeKind Kind, double OffsetMs);

/// <summary>An overstrum or overhit, where it happened, for the lane to mark.</summary>
public readonly record struct StrayMark(JudgeKind Kind, double Song, int Lane);

/// <summary>
/// A playtest of one track (charter-spec C19 item 4): an engine fed timed actions, and what it said, kept for the editor to
/// draw and list. Each pass over the loop region starts its own stats; problems add up over the passes.
/// </summary>
/// <remarks>
/// The engine is advanced only to <see cref="Horizon"/> before the song, so an input from a slower device that arrives
/// after a later one from a faster device is still judged before its note is missed. Belongs to the UI thread.
/// </remarks>
public sealed class PlaytestRun : IActionSink
{
    /// <summary>How far behind the song the engine is advanced, in real seconds.</summary>
    public const double Horizon = 0.03;

    private const int RecentCapacity = 64;

    private readonly List<JudgeEvent> _output = [];
    private readonly List<JudgeEvent> _recent = [];
    private readonly Dictionary<(long Tick, int Lane), NoteMark> _marks = [];
    private readonly List<StrayMark> _strays = [];
    private readonly Dictionary<(long Tick, int Lane, JudgeKind Kind), PlaytestProblem> _problems = [];
    private PlaytestProblem[]? _sortedProblems;

    public PlaytestRun(IPlaytestEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>Raised after anything is judged.</summary>
    public event EventHandler? Changed;

    public IPlaytestEngine Engine { get; private set; }

    public PlaytestChart Chart => Engine.Chart;

    /// <summary>This pass's counts.</summary>
    public PlaytestStats Pass { get; private set; } = new();

    /// <summary>Every pass's counts together.</summary>
    public PlaytestStats Total { get; } = new();

    /// <summary>How many passes have started.</summary>
    public int Passes { get; private set; }

    /// <summary>The latest things judged, the latest last.</summary>
    public IReadOnlyList<JudgeEvent> Recent => _recent;

    /// <summary>How each note was played the last time it was judged, by tick and game view lane, each lane of a chord apart.</summary>
    public IReadOnlyDictionary<(long Tick, int Lane), NoteMark> Marks => _marks;

    /// <summary>The overstrums and overhits since each place was last played.</summary>
    public IReadOnlyList<StrayMark> Strays => _strays;

    /// <summary>Every problem, in tick order.</summary>
    public IReadOnlyList<PlaytestProblem> Problems =>
        _sortedProblems ??= [.. _problems.Values.OrderBy(static p => p.Tick).ThenBy(static p => p.Lane)];

    /// <summary>
    /// Starts judging from <paramref name="song"/>: as play starts, after a seek, or when the loop jumps back. Overstrums and
    /// overhits from there on are taken back, as that stretch is played again.
    /// </summary>
    /// <param name="newPass">Whether this is a new pass, with its own stats.</param>
    public void Start(double song, double speed, Func<InstrumentAction, bool> isDown, bool newPass)
    {
        ArgumentNullException.ThrowIfNull(isDown);
        Engine.Reset(song, speed, isDown);
        if (newPass || Passes == 0)
        {
            Pass = new PlaytestStats();
            Passes++;
        }

        _strays.RemoveAll(m => m.Song >= song);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Judges a new chart from now on, after an edit: notes are kept where they were, and the marks and problems of notes
    /// no longer there are dropped.
    /// </summary>
    public void Replace(IPlaytestEngine engine, double song, double speed, Func<InstrumentAction, bool> isDown)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(isDown);
        Engine = engine;
        engine.Reset(song, speed, isDown);

        var places = new HashSet<(long, int)>();
        foreach (var note in engine.Chart.Notes)
        {
            foreach (int lane in note.Lanes)
                places.Add((note.Tick, lane));
        }

        foreach (var key in _marks.Keys.Where(k => !places.Contains(k)).ToList())
            _marks.Remove(key);
        foreach (var key in _problems.Keys.Where(k => k.Kind is JudgeKind.Miss or JudgeKind.SustainDrop && !places.Contains((k.Tick, k.Lane))).ToList())
            _problems.Remove(key);
        _sortedProblems = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public void Handle(in TimedAction action)
    {
        Engine.Input(action, _output);
        Apply();
    }

    /// <summary>Judges what time alone settles, up to <see cref="Horizon"/> before <paramref name="song"/>.</summary>
    public void Advance(double song, double speed)
    {
        Engine.Advance(song - (Horizon * speed), speed, _output);
        Apply();
    }

    private void Apply()
    {
        if (_output.Count == 0)
            return;

        foreach (var judged in _output)
        {
            Pass.Add(judged);
            Total.Add(judged);
            _recent.Add(judged);
            Record(judged);
        }

        if (_recent.Count > RecentCapacity)
            _recent.RemoveRange(0, _recent.Count - RecentCapacity);
        _output.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Record(in JudgeEvent judged)
    {
        switch (judged.Kind)
        {
            case JudgeKind.Hit or JudgeKind.Miss or JudgeKind.SustainDrop:
            {
                var note = Chart.Notes[judged.Note];
                foreach (int lane in note.Lanes)
                    _marks[(note.Tick, lane)] = new NoteMark(judged.Kind, judged.OffsetMs);
                if (judged.Kind != JudgeKind.Hit)
                    Count(note.Tick, note.Lanes[0], judged.Kind, judged.OffsetMs);
                break;
            }

            case JudgeKind.Overstrum or JudgeKind.Overhit:
                _strays.Add(new StrayMark(judged.Kind, judged.Song, judged.Lane));
                Count((long)Math.Round(Chart.TickAt(judged.Song)), judged.Lane, judged.Kind, 0);
                break;
        }
    }

    private void Count(long tick, int lane, JudgeKind kind, double offsetMs)
    {
        var key = (tick, lane, kind);
        _problems[key] = _problems.TryGetValue(key, out var had)
            ? had with { Count = had.Count + 1, OffsetMs = offsetMs }
            : new PlaytestProblem(tick, lane, kind, 1, offsetMs);
        _sortedProblems = null;
    }
}
