using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.Core.Validation.QuickFixes;

namespace ChartShared.Core.Charting.Editing;

/// <summary>The editing tools of the toolbar (charter-spec §4.5).</summary>
public enum EditorTool
{
    Select,
    Place,
    Erase,
    Sustain,
    Phrase,
    Tempo,

    /// <summary>Drags out a synchronized region (<see cref="SyncRegion"/>).</summary>
    Sync,
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
/// <remarks>
/// <paramref name="Lane"/> is the column the pointer is in, as drawn: inside a disco flip section of Pro drums, the pad
/// the player hits, which the tools map back to the pad stored (<see cref="EditorSession.StoredLane"/>).
/// </remarks>
public readonly record struct ChartPoint(ChartTrackKey Track, double Tick, int? Lane, double Tolerance, LaneSlots Slots, RailKind? Rail = null);

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
/// <para>Where <c>[music_start]</c>, <c>[music_end]</c> and <c>[end]</c> go is worked out again after every step done,
/// undone or redone, and as the audio heard or the delay changes (<see cref="SongEventMarks"/>).</para>
/// </remarks>
public sealed class EditorSession
{
    private readonly Dictionary<ChartTrackKey, NoteTrack> _game = [];
    private IReadOnlyList<KeyValuePair<string, string>> _songIni;
    private IChartCommand? _preview;
    private ChartReadOptions? _readOptions;
    private GameDiscoFlips? _discoFlips;
    private readonly Dictionary<ChartDifficulty, IReadOnlyList<DiscoFlipRange>> _discoFlipRanges = [];
    private AudibleRange? _audible;

    // Each vocal part as last read, with the track it was read from: a track is never changed, only replaced (re-ticked,
    // or put back by an undo), so a part is read again only when its track isn't the one it was read from.
    private readonly Dictionary<VocalTrack, (PassthroughTrack Track, VocalTrackContent? Content, VocalLane Lane)> _vocals = [];

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
        Selection = new EditorSelection(Chart, VocalContentOf);
        Tools = new EditorTools(this);
        Fills = new FillProposals(this);
        TempoProposal = new TempoProposals(this);
        Reduce = new ReduceProposals(this);
        Sync = new SyncRegion(this);
        SongEventMarks = SongEventsWith(null);
        undo.Changed += OnUndoChanged;
    }

    /// <summary>Raised after the chart changes: a command done, undone or redone, or a preview applied or taken back.</summary>
    public event EventHandler<EditorChartChangedEventArgs>? ChartChanged;

    /// <summary>Raised when the snap, the tool, the focused lane, the status or the proposed fills change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised each time a status is said, even one the same as the last, so a refusal repeated is said again.</summary>
    public event EventHandler<string>? StatusPosted;

    /// <summary>Raised when the song's delay or <c>song.ini</c> changes (<see cref="SetSongSettings"/>), which moves or rereads every track.</summary>
    public event EventHandler? SongSettingsChanged;

    /// <summary>Raised when <see cref="SongEventMarks"/> changes.</summary>
    public event EventHandler? SongEventsPlaced;

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
        ForgetDiscoFlips();
        Fills.OnChanged();
        SongSettingsChanged?.Invoke(this, EventArgs.Empty);
        PlaceSongEvents();
        RaiseState();
    }

    /// <summary>
    /// Where the editor puts <c>[music_start]</c>, <c>[music_end]</c> and <c>[end]</c> as the chart now is, against the audio
    /// heard as last measured (<see cref="SetAudible"/>): charter-spec C8 item 5. The chart never holds them; a save puts
    /// them in what it writes.
    /// </summary>
    public SongEventMarks SongEventMarks { get; private set; }

    /// <summary>Where the audio is heard, as last measured; null with no audio, none of it heard, or none measured yet.</summary>
    public AudibleRange? Audible => _audible;

    /// <summary>
    /// Takes up where the audio is heard, once it's measured: after it's opened, or its tracks change. The last range
    /// measured is kept while the tracks are read again, so the song events don't jump to the notes and back.
    /// </summary>
    public void SetAudible(AudibleRange? range)
    {
        if (range == _audible)
            return;
        _audible = range;
        PlaceSongEvents();
    }

    /// <summary>Where the placed song events go as the chart now is, against <paramref name="audio"/>, the vocals counted as notes.</summary>
    public SongEventMarks SongEventsWith(AudibleRange? audio) => SongEventPlacement.Place(Chart, audio, Delay, VocalSpan());

    // The first vocal note's tick and the last one's end, across the vocal parts; null for none.
    private (long First, long End)? VocalSpan()
    {
        long? first = null, end = null;
        foreach (var track in Enum.GetValues<VocalTrack>())
        {
            if (VocalsOf(track) is not { Notes.Count: > 0 } lane)
                continue;
            if (first is null || lane.Notes[0].Tick < first)
                first = lane.Notes[0].Tick;
            foreach (var note in lane.Notes)
            {
                if (end is null || note.EndTick > end)
                    end = note.EndTick;
            }
        }

        return first is { } f && end is { } e ? (f, e) : null;
    }

    // Works out the placed song events again, saying so when they've moved.
    private void PlaceSongEvents()
    {
        var marks = SongEventsWith(_audible);
        if (marks == SongEventMarks)
            return;
        SongEventMarks = marks;
        SongEventsPlaced?.Invoke(this, EventArgs.Empty);
    }

    public EditorViewport Viewport { get; }

    public EditorSelection Selection { get; }

    /// <summary>The tools, which turn clicks, drags and keys into commands.</summary>
    public EditorTools Tools { get; }

    /// <summary>The run of Generate fills going on, if there's one (charter-spec C6 item 5).</summary>
    public FillProposals Fills { get; }

    /// <summary>The proposed tempo map being looked at, if there's one (charter-spec C14 item 4).</summary>
    public TempoProposals TempoProposal { get; }

    /// <summary>The run of Reduce going on, if there's one (charter-spec C9 item 2).</summary>
    public ReduceProposals Reduce { get; }

    /// <summary>The synchronized region, if one is drawn: edits in it are made again in the passages that matched it.</summary>
    public SyncRegion Sync { get; }

    /// <summary>
    /// The notes drawn faintly over <paramref name="key"/>'s lane as a draft not yet added: a recording pass's, a Reduce
    /// run's, or null for none.
    /// </summary>
    public EditableTrack? GhostTrack(ChartTrackKey key) =>
        Recording?.Track == key ? Recording.Ghost : Reduce.DraftFor(key)?.Ghost;

    /// <summary>The pass of record mode going on, if there's one (charter-spec C15 item 5).</summary>
    public RecordPass? Recording { get; private set; }

    /// <summary>
    /// Starts a pass of record mode into <paramref name="track"/>, in place of any going on, which is cancelled: from
    /// <paramref name="startSeconds"/> of audio, as the editor draws the song.
    /// </summary>
    /// <param name="punch">The range to record into when overwriting; null for the stretch the pass plays over.</param>
    /// <param name="quantize">The grid presses go to (<see cref="RecordPass.Quantize"/>); null to keep the time they're played at.</param>
    public RecordPass StartRecording(ChartTrackKey track, RecordMode mode, double startSeconds, (long From, long To)? punch = null, SnapGrid? quantize = null)
    {
        Tools.Cancel();
        Recording?.Cancel();
        Recording = new RecordPass(this, track, mode, startSeconds, mode == RecordMode.Overwrite ? punch : null) { Quantize = quantize };
        RaiseState();
        return Recording;
    }

    /// <summary>Forgets <paramref name="pass"/>, as it ends.</summary>
    internal void EndRecording(RecordPass pass)
    {
        if (!ReferenceEquals(Recording, pass))
            return;
        Recording = null;
        RaiseState();
    }

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

    /// <summary>The tool a press in the lanes uses. Changing it keeps the selection.</summary>
    public EditorTool Tool
    {
        get;
        set
        {
            if (field == value)
                return;
            Tools.Cancel();
            if (value == EditorTool.Sync)
                ToolBeforeSync = field;
            field = value;
            RaiseState();
        }
    }

    /// <summary>The tool picked before the Sync tool, which a region drawn goes back to.</summary>
    internal EditorTool ToolBeforeSync { get; private set; }

    /// <summary>
    /// The kind of phrase the Phrase tool draws, of those offered for the focused part (<see cref="ToolKindsOf"/>), or disco
    /// flip on Pro drums. When the focus moves to a part that isn't offered it, or the drums stop being Pro, it goes back
    /// to star power.
    /// </summary>
    public RailKind PhraseKind
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

    /// <summary>The tracks of the lanes shown, which a range selected across lanes covers (charter-spec C15 item 2); null for none known.</summary>
    public Func<IReadOnlyList<ChartTrackKey>>? VisibleTracks { get; set; }

    /// <summary>The onsets found in the lanes' audio, for snapping and nudging to them; null without audio (charter-spec C14).</summary>
    public IOnsetSource? Onsets { get; set; }

    /// <summary>
    /// Whether a note placed or moved goes to the nearest onset within half a grid step, rather than to the grid
    /// (charter-spec C14 item 2, <c>O</c>). The grid still wins where no onset is that near.
    /// </summary>
    public bool SnapToOnset
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

    /// <summary>The lane the keys act on: the part and difficulty last clicked, or chosen.</summary>
    public ChartTrackKey? Focused
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            KeepPhraseKindOffered();
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

    /// <summary>Whether the drums are Pro, the only drums the games flip (charter-spec C6 item 7).</summary>
    public bool IsProDrums => Chart.DrumsVariant is { } variant ? variant == DrumsVariant.Pro : DiscoFlips.IsProDrums;

    /// <summary>How the drums are charted, as the editor draws them (<see cref="IsFiveLaneDrums"/>, <see cref="IsProDrums"/>).</summary>
    public DrumsVariant DrumsVariantShown => IsFiveLaneDrums ? DrumsVariant.FiveLane : IsProDrums ? DrumsVariant.Pro : DrumsVariant.FourLane;

    /// <summary>
    /// Where the games swap red and yellow on the drums (charter-spec C6 item 7), as the game view reads it. Worked out
    /// again only after an edit touches the drums.
    /// </summary>
    public GameDiscoFlips DiscoFlips => _discoFlips ??= GameView.DiscoFlips(Chart, ReadOptions);

    /// <summary>
    /// The disco flip sections of <paramref name="key"/>'s track (<see cref="Charting.DiscoFlips.RangesOf"/>), for the rail
    /// to draw and hit-test on every frame and pointer move; none when it isn't a drums track. Worked out again only after
    /// an edit touches the drums.
    /// </summary>
    public IReadOnlyList<DiscoFlipRange> DiscoFlipRangesOf(ChartTrackKey key)
    {
        if (ChartInstruments.KindOf(key.Instrument) != ChartInstrumentKind.Drums || TrackOf(key) is not { } track)
            return [];
        if (!_discoFlipRanges.TryGetValue(key.Difficulty, out var ranges))
        {
            ranges = Charting.DiscoFlips.RangesOf(track);
            _discoFlipRanges[key.Difficulty] = ranges;
        }

        return ranges;
    }

    /// <summary>
    /// The lane a note stored in <paramref name="lane"/> at <paramref name="tick"/> of <paramref name="key"/>'s track is
    /// drawn in: the pad the player hits (charter-spec C6 item 7). Where the games flip the drums (<see cref="DiscoFlips"/>),
    /// red and yellow swap; everywhere else, and for every other lane, it's <paramref name="lane"/>. The canvases draw by
    /// this, and the tools map the pointer's column back by <see cref="StoredLane"/>.
    /// </summary>
    public int ShownLane(ChartTrackKey key, long tick, int lane) =>
        lane is 1 or 2 && ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums && DiscoFlips.IsFlipped(key.Difficulty, tick) ? 3 - lane : lane;

    /// <summary>The lane <paramref name="note"/> of <paramref name="key"/>'s track is drawn in (<see cref="ShownLane(ChartTrackKey, long, int)"/>).</summary>
    public int ShownLane(ChartTrackKey key, EditableNote note) => ShownLane(key, note.Tick, note.Lane);

    /// <summary>
    /// The lane a note drawn in <paramref name="shown"/> at <paramref name="tick"/> of <paramref name="key"/>'s track is
    /// stored in: the pad drawn there, as <see cref="ShownLane(ChartTrackKey, long, int)"/> draws it. Swapping red and
    /// yellow undoes itself, so it's the same mapping: a note placed in the yellow column inside a section is red.
    /// </summary>
    public int StoredLane(ChartTrackKey key, long tick, int shown) => ShownLane(key, tick, shown);

    /// <summary>
    /// The kinds the Phrase tool offers for <paramref name="instrument"/>'s part (charter-spec C8): its rail's rows, with
    /// disco flip only on Pro drums (<see cref="PhraseRail.ToolKindsOf"/>).
    /// </summary>
    public IReadOnlyList<RailKind> ToolKindsOf(ChartInstrument instrument)
    {
        var kind = ChartInstruments.KindOf(instrument);
        return PhraseRail.ToolKindsOf(kind, kind == ChartInstrumentKind.Drums && IsProDrums);
    }

    /// <summary>Whether a gesture's preview is applied to the chart.</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>The audio time of <paramref name="tick"/>, in seconds.</summary>
    public double AudioAt(double tick) => TempoMap.SecondsAt(tick) + Delay.TotalSeconds;

    /// <summary>The tick, unrounded, at <paramref name="audioSeconds"/> of audio time.</summary>
    public double TickAt(double audioSeconds) => TempoMap.TickAt(audioSeconds - Delay.TotalSeconds);

    /// <summary>The cursor's tick: the nearest step of the grid (or tick, with snap off), never before the start.</summary>
    public long CursorTick => Snap.Nearest(TickAt(Viewport.Cursor), TempoMap);

    /// <summary>
    /// Where a note placed, moved, pasted or recorded at <paramref name="tick"/> of <paramref name="key"/>'s track goes:
    /// the nearest onset within half a grid step with snap to onset on, otherwise the grid (charter-spec C14 item 2, C15
    /// item 5).
    /// </summary>
    public long Quantize(double tick, ChartTrackKey key) => Quantize(tick, key, Snap);

    /// <summary>
    /// Where a note at <paramref name="tick"/> of <paramref name="key"/>'s track goes on <paramref name="grid"/>: the nearest
    /// onset within half a step of it with snap to onset on, otherwise the grid's nearest step.
    /// </summary>
    public long Quantize(double tick, ChartTrackKey key, SnapGrid grid)
    {
        var map = TempoMap;
        if (SnapToOnset && Onsets?.OnsetsFor(key) is { Count: > 0 } onsets
            && OnsetSnap.Nearest(tick, onsets, map, Delay.TotalSeconds, grid) is { } onset)
            return onset;
        return grid.Nearest(tick, map);
    }

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

    /// <summary>
    /// The vocal part <paramref name="track"/> as its lane draws it, read from the chart's first track of that name
    /// (<see cref="VocalTracks.IndexIn"/>); null when the chart hasn't the track. A track that can't be read is an empty
    /// part. Read again only after its track is replaced: edited, re-ticked, or put back by an undo.
    /// </summary>
    public VocalLane? VocalsOf(VocalTrack track) => VocalOf(track)?.Lane;

    /// <summary>
    /// The vocal part <paramref name="track"/> as it's edited (charter-spec C3 item 7); null when the chart hasn't the
    /// track, or it can't be read, so it's kept as it is.
    /// </summary>
    public VocalTrackContent? VocalContentOf(VocalTrack track) => VocalOf(track)?.Content;

    private (VocalTrackContent? Content, VocalLane Lane)? VocalOf(VocalTrack track)
    {
        int index = VocalTracks.IndexIn(Chart, track);
        if (index < 0)
        {
            _vocals.Remove(track);
            return null;
        }

        var found = Chart.Passthrough[index];
        if (_vocals.TryGetValue(track, out var known) && ReferenceEquals(known.Track, found))
            return (known.Content, known.Lane);

        VocalTrackContent? content;
        VocalLane lane;
        try
        {
            content = VocalTrackContent.Of(found);
            lane = new VocalLane(content.ToPart());
        }
        catch (ChartFormatException)
        {
            content = null;
            lane = new VocalLane(new VocalPart(VocalTracks.NameOf(track), [], [], []));
        }

        _vocals[track] = (found, content, lane);
        return (content, lane);
    }

    /// <summary>Does <paramref name="command"/> as one undoable step. A refusal is said in <see cref="Status"/>.</summary>
    /// <returns>Whether it was done.</returns>
    public bool Execute(IChartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            UndoStack.Execute(Sync.IsActive ? Sync.WithCopies(command) : command);
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
    /// Applies the quick fix <paramref name="optionId"/> (<see cref="ChartFixOption.Id"/>) of each of
    /// <paramref name="findings"/> as one undoable step, each worked out against the chart the fixes before it leave, so
    /// one an earlier fix has already seen to proposes nothing and is passed over.
    /// </summary>
    /// <returns>How many fixes were applied, none when no finding has one left; null when one was refused, which <see cref="Status"/> says.</returns>
    public int? FixAll(IEnumerable<Finding> findings, string optionId)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(optionId);
        int fixes = 0;
        bool done = ExecuteAsOne(findings.Select(finding => (Func<(IChartCommand?, string?)>)(() =>
        {
            var command = QuickFixRegistry.ProposeChartFix(finding, Chart, ReadOptions, optionId);
            if (command is not null)
                fixes++;
            return (command, null);
        })));
        return done ? fixes : null;
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
        {
            Selection.Reconcile();
            KeepPhraseKindOffered();
            Fills.OnChanged();
            Reduce.OnChanged();
            Sync.OnChanged();
            PlaceSongEvents();
        }

        ChartChanged?.Invoke(this, new EditorChartChangedEventArgs(affected, isPreview));
    }

    // The Phrase tool's kind goes back to star power when the focused part isn't offered it.
    private void KeepPhraseKindOffered()
    {
        if (Focused is { } key && !ToolKindsOf(key.Instrument).Contains(PhraseKind))
            PhraseKind = Charting.PhraseKind.StarPower;
    }

    // Drops the game view of every track the change touched.
    private void Forget(ChartChange affected)
    {
        if (affected.RebuildsAllTracks)
        {
            _readOptions = null;
            _game.Clear();
            ForgetDiscoFlips();
            return;
        }

        if (affected.Regions.Any(static r => ChartInstruments.KindOf(r.Part) == ChartInstrumentKind.Drums))
            ForgetDiscoFlips();
        foreach (var key in _game.Keys.Where(affected.Touches).ToList())
            _game.Remove(key);
    }

    // Drops the flips and the sections worked out from the drums, once an edit has touched them or the song's settings changed.
    private void ForgetDiscoFlips()
    {
        _discoFlips = null;
        _discoFlipRanges.Clear();
    }

    internal void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);
}
