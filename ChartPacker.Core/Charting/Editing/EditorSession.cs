using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Editing;

/// <summary>The editing tools of the toolbar (charter-spec §4.5).</summary>
public enum EditorTool
{
    Select,
    Place,
    Erase,
    Sustain,
    Phrase,
    Tempo,
}

/// <summary>The modifier keys held with a click or a key.</summary>
[Flags]
public enum EditorModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// A point in a lane, as the editing tools see it whichever view the lane is in (charter-spec C3, AC C3.2).
/// </summary>
/// <param name="Track">The lane's part and difficulty.</param>
/// <param name="Tick">The tick under the pointer, unrounded.</param>
/// <param name="Lane">The note lane under the pointer (<see cref="EditableNote.Lane"/>), or null when it's outside the lanes.</param>
/// <param name="Tolerance">How many ticks from a note's start, or a phrase's end, the pointer still hits it.</param>
/// <param name="Slots">The lane's columns or rows.</param>
/// <param name="Rail">
/// The row of the phrase rail under the pointer (<see cref="PhraseRail"/>), by the kind it holds, or null when the pointer
/// isn't in the rail. <paramref name="Lane"/> is null when it is.
/// </param>
public readonly record struct ChartPoint(ChartTrackKey Track, double Tick, int? Lane, double Tolerance, LaneSlots Slots, PhraseKind? Rail = null);

/// <summary>A change to the chart the editor shows: one done, undone or redone, or a gesture's preview.</summary>
public sealed class EditorChartChangedEventArgs(ChartChange affected, bool isPreview) : EventArgs
{
    /// <summary>What the change touched.</summary>
    public ChartChange Affected { get; } = affected;

    /// <summary>Whether it's a gesture in progress, which isn't on the undo stack until the gesture ends.</summary>
    public bool IsPreview { get; } = isPreview;
}

/// <summary>
/// The editor's state over one chart (charter-spec §4.5, C3): the undo stack, the cursor, the selection, the snap and
/// the tool, and the game view each lane draws from. Both canvases and every lane share one session, so switching a lane
/// between the views keeps all of it (AC C3.1). It belongs to the editing thread, as the chart does.
/// </summary>
/// <remarks>
/// <para>Audio time is chart time plus the song's delay (§4.4.1); this is the one place the two are related.</para>
/// <para>A gesture, such as a drag, shows what it would do as it goes by applying a <b>preview</b> command straight to the
/// chart, then puts it on the undo stack as one step when it ends (<see cref="CommitPreview"/>).</para>
/// </remarks>
public sealed class EditorSession
{
    private readonly Dictionary<ChartTrackKey, NoteTrack> _game = [];
    private IReadOnlyList<KeyValuePair<string, string>> _songIni;
    private IChartCommand? _preview;
    private ChartReadOptions? _readOptions;

    /// <param name="undo">The chart's undo stack.</param>
    /// <param name="delay">The song's delay: how much later than its ticks say the chart plays against the audio.</param>
    /// <param name="songIni">The song's <c>song.ini</c>, for the HOPO threshold, the sustain cutoff and the drums' layout.</param>
    public EditorSession(ChartUndoStack undo, TimeSpan delay, IReadOnlyList<KeyValuePair<string, string>> songIni)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(songIni);
        UndoStack = undo;
        Delay = delay;
        _songIni = songIni;
        Viewport = new EditorViewport();
        Selection = new EditorSelection(Chart);
        Tools = new EditorTools(this);
        undo.Changed += OnUndoChanged;
    }

    /// <summary>Raised after the chart changes: a command done, undone or redone, or a preview applied or taken back.</summary>
    public event EventHandler<EditorChartChangedEventArgs>? ChartChanged;

    /// <summary>Raised when the snap, the tool, the focused lane or the status changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised each time a status is said, even one the same as the last, so a refusal repeated is said again.</summary>
    public event EventHandler<string>? StatusPosted;

    /// <summary>Raised when the song's delay or <c>song.ini</c> changes (<see cref="SetSongSettings"/>), which moves or rereads every track.</summary>
    public event EventHandler? SongSettingsChanged;

    public EditableChart Chart => UndoStack.Chart;

    public ChartUndoStack UndoStack { get; }

    /// <summary>The song's delay: audio time is chart time plus this.</summary>
    public TimeSpan Delay { get; private set; }

    /// <summary>
    /// Takes up the song's <c>song.ini</c> and delay as they now are: after a save, or a pending change reverted or
    /// restored. Every track's game view is read again.
    /// </summary>
    public void SetSongSettings(IReadOnlyList<KeyValuePair<string, string>> songIni, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(songIni);
        var options = ChartReadOptions.From(songIni);
        if (delay == Delay && options == ChartReadOptions.From(_songIni))
        {
            _songIni = songIni;
            return;
        }

        _songIni = songIni;
        Delay = delay;
        _readOptions = null;
        _game.Clear();
        SongSettingsChanged?.Invoke(this, EventArgs.Empty);
        RaiseState();
    }

    public EditorViewport Viewport { get; }

    public EditorSelection Selection { get; }

    /// <summary>The tools, which turn clicks, drags and keys into commands.</summary>
    public EditorTools Tools { get; }

    /// <summary>The tempo map the chart's ticks are timed by.</summary>
    public TempoMap TempoMap => Chart.TempoMap.ToTempoMap();

    public SnapGrid Snap
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            RaiseState();
        }
    } = SnapGrid.Default;

    public EditorTool Tool
    {
        get;
        set
        {
            if (field == value)
                return;
            Tools.Cancel();
            field = value;
            RaiseState();
        }
    }

    /// <summary>
    /// The kind of phrase the Phrase tool draws, of those the focused part can have (<see cref="PhraseRail.KindsOf"/>).
    /// When the focus moves to a part that can't have it, it goes back to star power.
    /// </summary>
    public PhraseKind PhraseKind
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            RaiseState();
        }
    }

    /// <summary>Whether notes keep their times rather than their ticks when the tempo changes (charter-spec C4 item 3).</summary>
    public bool KeepTimes { get; set; }

    /// <summary>The lane the keys act on: the part and difficulty last clicked, or chosen.</summary>
    public ChartTrackKey? Focused
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            if (value is { } key && !PhraseRail.KindsOf(ChartInstruments.KindOf(key.Instrument)).Contains(PhraseKind))
                PhraseKind = PhraseKind.StarPower;
            RaiseState();
        }
    }

    /// <summary>What the editor has to say: why an edit was refused, or what a key did. Null for nothing.</summary>
    public string? Status
    {
        get;
        set
        {
            // Said again even when it's the same: a refusal repeated is news, though the status hasn't changed.
            if (value is not null)
                StatusPosted?.Invoke(this, value);
            if (field == value)
                return;
            field = value;
            RaiseState();
        }
    }

    /// <summary>The song.ini settings the chart is read with once saved, which the game view is worked out with.</summary>
    public ChartReadOptions ReadOptions => _readOptions ??= MidiChartWriter.SettingsAfterSave(Chart, ChartReadOptions.From(_songIni));

    /// <summary>Whether the drums are drawn with five pads.</summary>
    public bool IsFiveLaneDrums => Chart.DrumsVariant is { } variant ? variant == DrumsVariant.FiveLane : GameView.IsFiveLaneDrums(Chart, ReadOptions);

    /// <summary>Whether a gesture's preview is applied to the chart.</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>The audio time of <paramref name="tick"/>, in seconds.</summary>
    public double AudioAt(double tick) => TempoMap.SecondsAt(tick) + Delay.TotalSeconds;

    /// <summary>The tick, unrounded, at <paramref name="audioSeconds"/> of audio time.</summary>
    public double TickAt(double audioSeconds) => TempoMap.TickAt(audioSeconds - Delay.TotalSeconds);

    /// <summary>The cursor's tick: the nearest step of the grid (or tick, with snap off), never before the start.</summary>
    public long CursorTick => Snap.Nearest(TickAt(Viewport.Cursor), TempoMap);

    /// <summary>Moves the cursor to <paramref name="tick"/>.</summary>
    public void MoveCursorTo(long tick) => Viewport.MoveCursor(AudioAt(Math.Max(0, tick)));

    /// <summary>The columns or rows of <paramref name="instrument"/>'s lane in <paramref name="view"/>.</summary>
    public LaneSlots SlotsOf(ChartInstrument instrument, LaneView view) => LaneSlots.Of(ChartInstruments.KindOf(instrument), view, IsFiveLaneDrums);

    /// <summary>
    /// The track as the games read it (the game view, charter-spec §4.3): natural HOPOs, sustain cutoffs and star power,
    /// as the editor draws them. Worked out again only after an edit touches the track.
    /// </summary>
    public NoteTrack GameTrack(ChartTrackKey key)
    {
        if (!_game.TryGetValue(key, out var track))
        {
            track = GameView.Project(Chart, key, ReadOptions);
            _game[key] = track;
        }

        return track;
    }

    /// <summary>The track's notes as the editor has them, or null when the chart hasn't the part.</summary>
    public EditableTrack? TrackOf(ChartTrackKey key) => Chart.PartOf(key.Instrument)?[key.Difficulty];

    /// <summary>Does <paramref name="command"/> as one undoable step. A refusal is said in <see cref="Status"/>.</summary>
    /// <returns>Whether it was done.</returns>
    public bool Execute(IChartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            UndoStack.Execute(command);
            Status = null;
            return true;
        }
        catch (Exception e) when (IsRefusal(e))
        {
            Status = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Does the commands <paramref name="steps"/> make as one undoable step, each worked out against the chart the ones
    /// before it leave, such as removing several anchored tempo markers. A step that's refused, by returning its refusal
    /// or by throwing, leaves the chart as it was, and says why in <see cref="Status"/>.
    /// </summary>
    /// <returns>Whether they were done.</returns>
    public bool ExecuteAsOne(IEnumerable<Func<(IChartCommand? Command, string? Refusal)>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Tools.Cancel();
        var applied = new List<IChartCommand>();
        string? refusal = null;
        try
        {
            foreach (var step in steps)
            {
                var (command, why) = step();
                if (command is null)
                {
                    refusal = why;
                    if (refusal is not null)
                        break;
                    continue;
                }

                command.Apply(Chart);
                applied.Add(command);
            }
        }
        catch (Exception e) when (IsRefusal(e))
        {
            refusal = e.Message;
        }
        finally
        {
            // Taken back, to be done again as one step.
            for (int i = applied.Count - 1; i >= 0; i--)
                applied[i].Revert(Chart);
        }

        if (refusal is not null)
        {
            Status = refusal;
            return false;
        }

        return applied.Count == 0 || Execute(applied.Count == 1 ? applied[0] : new CompositeCommand(applied));
    }

    /// <summary>
    /// Applies <paramref name="command"/> as the gesture's preview, in place of the one before; null takes the preview
    /// back. A command that's refused leaves the preview before it, and says why in <see cref="Status"/>.
    /// </summary>
    /// <returns>Whether the command was applied.</returns>
    public bool Preview(IChartCommand? command)
    {
        var old = _preview;
        var touched = ChartChange.None;
        if (old is not null)
        {
            old.Revert(Chart);
            touched = old.Affected;
            _preview = null;
        }

        bool applied = true;
        if (command is not null)
        {
            try
            {
                command.Apply(Chart);
                _preview = command;
                touched = touched.Merge(command.Affected);
                Status = null;
            }
            catch (Exception e) when (IsRefusal(e))
            {
                Status = e.Message;
                applied = false;
                if (old is not null)
                {
                    old.Apply(Chart);
                    _preview = old;
                    touched = ChartChange.None;
                }
            }
        }

        if (!touched.IsEmpty)
            Changed(touched, isPreview: true);
        return applied;
    }

    /// <summary>Puts the preview on the undo stack as one step, as its gesture ends.</summary>
    /// <returns>The command, or null when there was no preview.</returns>
    public IChartCommand? CommitPreview()
    {
        var command = _preview;
        if (command is null)
            return null;
        command.Revert(Chart);
        _preview = null;
        return Execute(command) ? command : null;
    }

    /// <summary>Undoes the last step, after taking back any gesture in progress.</summary>
    public void Undo()
    {
        Tools.Cancel();
        if (UndoStack.CanUndo)
            UndoStack.Undo();
    }

    /// <summary>Redoes the last step undone, after taking back any gesture in progress.</summary>
    public void Redo()
    {
        Tools.Cancel();
        if (UndoStack.CanRedo)
            UndoStack.Redo();
    }

    /// <summary>Stops following the undo stack, as the editor closes.</summary>
    public void Detach() => UndoStack.Changed -= OnUndoChanged;

    /// <summary>Whether <paramref name="e"/> is a command refusing a change, before changing anything.</summary>
    internal static bool IsRefusal(Exception e) => e is InvalidOperationException or ArgumentException or ChartFormatException;

    private void OnUndoChanged(object? sender, ChartCommandEventArgs e) => Changed(e.Affected, isPreview: false);

    private void Changed(ChartChange affected, bool isPreview)
    {
        Forget(affected);
        if (!isPreview)
            Selection.Reconcile();
        ChartChanged?.Invoke(this, new EditorChartChangedEventArgs(affected, isPreview));
    }

    // Drops the game view of every track the change touched.
    private void Forget(ChartChange affected)
    {
        if ((affected.Song & (SongChanges.Parts | SongChanges.DrumsVariant | SongChanges.TempoMap)) != 0)
        {
            _readOptions = null;
            _game.Clear();
            return;
        }

        foreach (var region in affected.Regions)
        {
            bool wholePart = region.Difficulty is null || ChartInstruments.KindOf(region.Part) == ChartInstrumentKind.Drums;
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                if (wholePart || region.Difficulty == difficulty)
                    _game.Remove(new ChartTrackKey(region.Part, difficulty));
            }
        }
    }

    private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);
}
