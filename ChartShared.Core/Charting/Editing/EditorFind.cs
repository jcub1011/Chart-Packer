using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>
/// The passages of a track, or of a vocal part, that match the selected notes (Ctrl+F), found as <see cref="SyncRegion"/>
/// finds the passages to synchronize, and the one the cursor was last taken to. They're forgotten when the selection or the chart changes,
/// as they'd no longer be what's there. The session owns it (<see cref="EditorSession.Find"/>).
/// </summary>
public sealed class EditorFind
{
    private const string NothingSelected = "Select some notes to find the passages that match them.";

    private const string NoOtherMatch = "No other passage matches the selected notes.";

    private readonly EditorSession _session;
    private readonly List<long> _matches = [];
    private int _current = -1;

    internal EditorFind(EditorSession session)
    {
        _session = session;
        session.Selection.Changed += (_, _) => Clear();
        session.ChartChanged += (_, _) => Clear();
    }

    /// <summary>Raised when the passages found, or the one the cursor is at, change.</summary>
    public event EventHandler? Changed;

    /// <summary>The highway's track the passages are of; null with none found, or with a vocal part's found.</summary>
    public ChartTrackKey? Track { get; private set; }

    /// <summary>The vocal part the passages are of; null with none found, or with a highway's found.</summary>
    public VocalTrack? Vocal { get; private set; }

    /// <summary>The ticks of the selected notes found, from the first note's start up to the last's end; null with none found.</summary>
    public (long From, long To)? Span { get; private set; }

    /// <summary>Where each passage found starts, earliest first, the selected notes' own among them; each is as long as <see cref="Span"/>.</summary>
    public IReadOnlyList<long> Matches => _matches;

    /// <summary>Which passage the cursor was last taken to, or -1 for none.</summary>
    public int Current => _current;

    /// <summary>Whether there are passages to go to.</summary>
    public bool HasMatches => _matches.Count > 0;

    /// <summary>
    /// Finds the passages that match the selected notes and goes to the first after the cursor; with some found already,
    /// goes to the next. Says so when no notes are selected or nothing matches.
    /// </summary>
    public void Start()
    {
        if (HasMatches)
        {
            Step(1);
            return;
        }

        ChartTrackKey? key = null;
        VocalTrack? vocal = null;
        long from, to;
        IReadOnlyList<long> found;
        if (_session.Selection.Track is { } track && _session.Selection.Notes is { Count: > 0 } notes)
        {
            key = track;
            from = notes[0].Tick;
            to = notes.Max(static n => n.Tick + n.Length);
            found = _session.Sync.Find(track, from, to);
        }
        else if (_session.Selection.Vocal is { } part && _session.VocalContentOf(part) is { } content
            && content.Notes.Where(n => _session.Selection.Contains(part, n.Place)).ToList() is { Count: > 0 } sung)
        {
            vocal = part;
            from = sung.Min(static n => n.Tick);
            to = sung.Max(static n => n.EndTick);
            found = _session.Sync.Find(part, from, to);
        }
        else
        {
            _session.Status = NothingSelected;
            return;
        }

        if (found.Count == 0)
        {
            // The notes alone are no match to go to.
            _session.Status = NoOtherMatch;
            return;
        }

        Track = key;
        Vocal = vocal;
        Span = (from, Math.Max(to, from + SyncRegion.MinLength(_session.Chart.Resolution)));

        // The notes searched for are a match of their own, in their place among the others.
        _matches.AddRange(found.Append(from).Order());
        _current = -1;
        Step(1);
    }

    /// <summary>Takes the cursor to the next passage found after it, or the first from the last; the previous when <paramref name="direction"/> is negative.</summary>
    public void Step(int direction)
    {
        if (!HasMatches)
            return;
        // The cursor's tick comes back from audio time, which can land a tick off the match it was just taken to.
        long cursor = _session.CursorTick;
        if (_current >= 0 && Math.Abs(cursor - _matches[_current]) <= 1)
            cursor = _matches[_current];
        int index = direction >= 0
            ? _matches.FindIndex(m => m > cursor)
            : _matches.FindLastIndex(m => m < cursor);
        if (index < 0)
            index = direction >= 0 ? 0 : _matches.Count - 1;
        _current = index;
        _session.MoveCursorTo(_matches[index]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the passages found.</summary>
    public void Clear()
    {
        if (Track is null && Vocal is null)
            return;
        Track = null;
        Vocal = null;
        Span = null;
        _matches.Clear();
        _current = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
