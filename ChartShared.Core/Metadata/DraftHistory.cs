namespace ChartShared.Core.Metadata;

/// <summary>
/// The undo and redo of one song's <see cref="MetadataDraft"/>: a timeline of the states the draft has been in since it
/// was empty or saved, and where in it the draft is now. Each song has a history of its own, so undoing one song's
/// changes never touches another's, whatever edit made them.
/// </summary>
/// <remarks>
/// States are copies of the whole draft rather than commands, as a draft is small and many places change it: the
/// owner says when it has changed (<see cref="Record"/>) and doesn't need to know what changed. Undoing and redoing
/// copy a state back into the same draft object, which the rest of the app holds on to. It belongs to the thread that
/// changes the draft.
/// </remarks>
public sealed class DraftHistory
{
    /// <summary>How long a change may follow another with the same merge key and still be one undo step.</summary>
    public static readonly TimeSpan DefaultMergeWindow = TimeSpan.FromSeconds(1);

    /// <summary>How many steps a history keeps by default.</summary>
    public const int DefaultCapacity = 200;

    private readonly MetadataDraft _draft;
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private readonly TimeSpan _mergeWindow;

    // The states the draft has been in, oldest first, and which one it's in now.
    private readonly List<MetadataDraft> _states = [];
    private int _current;

    // What the state at _current was last recorded as, when, for merging the next change into it.
    private string? _lastKey;
    private DateTimeOffset _lastTime;

    /// <param name="draft">The song's draft, as it is now: the state undo can't go back past.</param>
    /// <param name="clock">The time merging goes by; the system clock when null.</param>
    /// <param name="capacity">How many steps to keep: undoing further than that isn't possible.</param>
    /// <param name="mergeWindow">How long a change may follow another with the same key and merge with it; <see cref="DefaultMergeWindow"/> when null.</param>
    /// <param name="baseline">The state undo can't go back past, when the history starts after the draft's first change; a copy of the draft as it is now when null.</param>
    public DraftHistory(MetadataDraft draft, TimeProvider? clock = null, int capacity = DefaultCapacity, TimeSpan? mergeWindow = null, MetadataDraft? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _draft = draft;
        _clock = clock ?? TimeProvider.System;
        _capacity = capacity;
        _mergeWindow = mergeWindow ?? DefaultMergeWindow;
        _states.Add((baseline ?? draft).Clone());
    }

    public bool CanUndo => _current > 0;

    public bool CanRedo => _current < _states.Count - 1;

    /// <summary>How many changes can be undone.</summary>
    public int UndoCount => _current;

    /// <summary>How many changes can be redone.</summary>
    public int RedoCount => _states.Count - 1 - _current;

    /// <summary>
    /// Keeps the draft as it is now as a step to undo to, once it's been changed. The steps that were undone are
    /// dropped.
    /// </summary>
    /// <param name="mergeKey">
    /// Names a run of changes that are one step, such as the many a drag makes: a change with the key the last had,
    /// soon enough after it, replaces that step's state rather than adding one. Null never merges.
    /// </param>
    /// <returns>False when the draft is as it was, so there's nothing to undo.</returns>
    public bool Record(string? mergeKey = null)
    {
        if (_draft.SameAs(_states[_current]))
            return false;

        var now = _clock.GetUtcNow();
        bool merges = mergeKey is not null
            && mergeKey == _lastKey
            && _current > 0
            && _current == _states.Count - 1
            && now - _lastTime <= _mergeWindow;

        if (merges)
        {
            // A drag back to where it started leaves nothing to undo.
            _states[_current].CopyFrom(_draft);
            if (_states[_current].SameAs(_states[_current - 1]))
            {
                _states.RemoveAt(_current);
                _current--;
                _lastKey = null;
                return true;
            }
        }
        else
        {
            _states.RemoveRange(_current + 1, _states.Count - 1 - _current);
            _states.Add(_draft.Clone());
            _current++;
            if (_states.Count > _capacity + 1)
            {
                _states.RemoveAt(0);
                _current--;
            }
        }

        _lastKey = mergeKey;
        _lastTime = now;
        return true;
    }

    /// <summary>Puts the draft back as it was before the last change recorded.</summary>
    /// <returns>Whether only metadata keys differ from before, as the rest of what's shown needn't be read again then.</returns>
    /// <exception cref="InvalidOperationException">There's nothing to undo.</exception>
    public bool Undo()
    {
        if (!CanUndo)
            throw new InvalidOperationException("There's nothing to undo.");
        return MoveTo(_current - 1);
    }

    /// <summary>Applies again the last change undone.</summary>
    /// <returns>Whether only metadata keys differ from before.</returns>
    /// <exception cref="InvalidOperationException">There's nothing to redo.</exception>
    public bool Redo()
    {
        if (!CanRedo)
            throw new InvalidOperationException("There's nothing to redo.");
        return MoveTo(_current + 1);
    }

    /// <summary>Forgets every step, so that the draft as it is now is the state to undo to: for when it's saved or cleared for good.</summary>
    public void Reset()
    {
        _states.Clear();
        _states.Add(_draft.Clone());
        _current = 0;
        _lastKey = null;
    }

    private bool MoveTo(int index)
    {
        bool onlyKeys = _states[index].SameApartFromKeys(_draft);
        _current = index;
        _draft.CopyFrom(_states[index]);

        // Whatever was being merged into is done with.
        _lastKey = null;
        return onlyKeys;
    }
}
