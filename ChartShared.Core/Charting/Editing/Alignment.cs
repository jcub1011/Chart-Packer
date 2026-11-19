using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What a press of the Alignment tool takes hold of: the start's handle, the end's, or the notes between.</summary>
public enum AlignGrip
{
    /// <summary>The notes, to shift them all in time.</summary>
    Body,

    /// <summary>The handle before the first note, to stretch the notes with the last one's end kept where it is.</summary>
    Start,

    /// <summary>The handle after the last note's end, to stretch the notes with the first one kept where it is.</summary>
    End,
}

/// <summary>
/// The Alignment tool's shift and stretch of the part in focus (charter-spec C3 item 7), for notes or lyrics timed to other
/// audio: the session owns it (<see cref="EditorSession.Alignment"/>). With a vocal lane in focus it aligns every vocal
/// part, or only the one in focus; with an instrument's lane in focus, every difficulty of the instrument that has notes,
/// or only the one in focus. What it aligns is moved in time by <see cref="Offset"/> and played at <see cref="Speed"/>, as
/// one undoable step each time they change.
/// </summary>
/// <remarks>
/// <para>The parts are aligned in the audio's time, not in ticks, so a tempo map that changes doesn't bend them: a time
/// <c>t</c> goes to <c>start + Offset + (t − start) / Speed</c>, where <c>start</c> is when the first note (or lyric) was,
/// and every tick moved is put on the tick nearest its new time, never before the song starts. Only the tool's drag
/// snaps, and only the edge it drags; the offset and speed given here are kept as they are.</para>
/// <para>On the vocal parts each note's start and end, each lyric, each phrase's ends and each star power phrase's ends
/// move. On an instrument each note's start and end, each chord's forcing or tap and each drum mix (disco flip) move on
/// the difficulties aligned; with <see cref="AllDifficulties"/> the phrases every difficulty shares (star power, solos,
/// drum fills and big rock endings, rolls and trills) and the tom markers move too, and are left where they are without
/// it, as the difficulties not aligned still have them. Notes, or chord markings, that land on one another's place, as a
/// faster speed draws them together, are kept once, and the status says how many were. Everything else in a part's track
/// is left where it is.</para>
/// <para><see cref="Offset"/> and <see cref="Speed"/> are kept from the parts as they were when the tool was first used
/// on them: each change is worked out from those parts again, not from the last change, so typing a speed means that
/// speed of the parts as they were, and rounding to ticks doesn't build up. They're forgotten, and the parts as they are
/// taken as the start again, once the parts are changed some other way or the focus moves to other parts; undoing and
/// redoing the tool's own changes keeps them, each step's values coming back with it.</para>
/// </remarks>
public sealed class Alignment
{
    /// <summary>The slowest the parts may be played: half speed, twice as long.</summary>
    public const double MinSpeed = 0.5;

    /// <summary>The fastest the parts may be played: twice speed, half as long.</summary>
    public const double MaxSpeed = 2.0;

    /// <summary>What the status says when a press or a change finds nothing to align.</summary>
    internal const string NothingToAlign = "There are no notes or lyrics to align.";

    private readonly EditorSession _session;

    // The parts as they were when the values were last taken from nothing, which every change is worked out from, and
    // which parts the focus and options picked then.
    private Target _baseline = Nothing.Instance;
    private Scope _scope;

    // The values each change made, with what tells the parts as it left them, so undoing or redoing one brings its values
    // back. The first is the baseline's, no shift at full speed.
    private readonly List<(double Offset, double Speed, Print Parts)> _states = [];

    // Set while the tool's own change is being made, so the change isn't taken for one made some other way.
    private bool _applying;
    private bool _previewing;

    // Whose preview is shown: the drag's, or the toolbar's fields'.
    private object? _previewBy;

    internal Alignment(EditorSession session) => _session = session;

    /// <summary>The offset or speed changed, or the parts aligned did: what the tool shows is to be drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether every vocal part is aligned together, as it is by default, rather than only the one in focus.</summary>
    public bool AllVocalParts
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Restart();
        }
    } = true;

    /// <summary>
    /// Whether every difficulty of the instrument in focus is aligned together, with the phrases and tom markers they
    /// share, as it is by default, rather than only the difficulty in focus.
    /// </summary>
    public bool AllDifficulties
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Restart();
        }
    } = true;

    /// <summary>How far, in seconds, the first note is moved: later when positive.</summary>
    public double Offset { get; private set; }

    /// <summary>How fast the parts are played, 1 as they were: above 1 they take less time, below it more.</summary>
    public double Speed { get; private set; } = 1;

    /// <summary>The vocal parts being aligned, in order; none while an instrument is.</summary>
    public IReadOnlyList<VocalTrack> VocalParts => _baseline is Vocals vocals ? [.. vocals.Parts.Keys.Order()] : [];

    /// <summary>The instrument's tracks being aligned, Easy to Expert; none while the vocal parts are.</summary>
    public IReadOnlyList<ChartTrackKey> Tracks =>
        _baseline is Instrument instrument ? [.. instrument.Tracks.Keys.Order().Select(d => new ChartTrackKey(instrument.Part, d))] : [];

    /// <summary>Whether <paramref name="track"/> is one of the vocal parts being aligned.</summary>
    public bool Aligns(VocalTrack track) => _baseline is Vocals vocals && vocals.Parts.ContainsKey(track);

    /// <summary>Whether <paramref name="key"/> is one of the instrument's tracks being aligned.</summary>
    public bool Aligns(ChartTrackKey key) =>
        _baseline is Instrument instrument && instrument.Part == key.Instrument && instrument.Tracks.ContainsKey(key.Difficulty);

    /// <summary>
    /// When the parts' first note (or lyric) was, and their last note's end, in seconds of audio, before the offset and
    /// speed: what <see cref="Span"/> is worked out from. Null with nothing to align.
    /// </summary>
    public (double Start, double End)? BaseSpan
    {
        get
        {
            if (_baseline.Ticks is not var (from, to))
                return null;
            double start = _session.AudioAt(from);
            return (start, Math.Max(_session.AudioAt(to), start));
        }
    }

    /// <summary>Where the handles are: the first note and the last note's end, in seconds of audio, as aligned. Null with nothing to align.</summary>
    public (double Start, double End)? Span => BaseSpan is { } span ? SpanOf(span, Offset, Speed) : null;

    /// <summary>
    /// The earliest the first note may be moved to, as an offset: the song's start, so no note is moved before it. Null
    /// with nothing to align.
    /// </summary>
    public double? MinOffset => BaseSpan is { } span ? Math.Min(0, _session.AudioAt(0) - span.Start) : null;

    /// <summary>
    /// What a press at <paramref name="tick"/> takes hold of: the start's handle up to <paramref name="tolerance"/> ticks
    /// after the first note and three times that before it, the end's likewise either side of the last note's end, and the
    /// notes anywhere else. Null with nothing to align.
    /// </summary>
    public AlignGrip? GripAt(double tick, double tolerance)
    {
        if (Span is not { } span)
            return null;
        double from = _session.TickAt(span.Start), to = _session.TickAt(span.End);
        if (tick >= from - (3 * tolerance) && tick <= from + tolerance && tick < (from + to) / 2)
            return AlignGrip.Start;
        if (tick >= to - tolerance && tick <= to + (3 * tolerance))
            return AlignGrip.End;
        return AlignGrip.Body;
    }

    /// <summary>Whether a change is shown that isn't on the undo stack yet (<see cref="Preview"/>).</summary>
    public bool IsPreviewing => _previewing;

    /// <summary>Who showed the change that isn't on the undo stack yet, as <see cref="Preview"/> was told; null with none shown.</summary>
    public object? PreviewedBy => _previewing ? _previewBy : null;

    /// <summary>
    /// Shows the parts moved by <paramref name="offset"/> seconds and played at <paramref name="speed"/>, each kept in
    /// bounds, without putting it on the undo stack: as a drag or a scrubbed field goes on. <see cref="Commit"/> keeps it.
    /// </summary>
    /// <param name="by">Who's showing it, as <see cref="PreviewedBy"/> tells, so one doesn't keep another's.</param>
    /// <returns>Whether it could be shown.</returns>
    public bool Preview(double offset, double speed, object? by = null)
    {
        (offset, speed) = Bounded(offset, speed);

        // Worked out from the parts as they are without the last preview, as each preview is.
        _applying = true;
        try
        {
            _session.Preview(null);
        }
        finally
        {
            _applying = false;
        }

        _previewing = false;
        _previewBy = null;
        if (CommandFor(offset, speed, out var leftOut) is not { } command)
        {
            Show(offset, speed);
            return true;
        }

        _applying = true;
        try
        {
            if (!_session.Preview(command))
                return false;
        }
        finally
        {
            _applying = false;
        }

        _previewing = true;
        _previewBy = by;
        SayLeftOut(leftOut);
        Show(offset, speed);
        return true;
    }

    /// <summary>
    /// Puts what <see cref="Preview"/> showed on the undo stack as one step, as a drag ends or a field is settled. With
    /// nothing shown, the values go back to the parts' own.
    /// </summary>
    /// <returns>Whether anything was changed.</returns>
    public bool Commit()
    {
        if (!_previewing)
        {
            Reconcile();
            return false;
        }

        double offset = Offset, speed = Speed;
        _previewing = false;
        _previewBy = null;
        _session.Preview(null);
        return Apply(offset, speed);
    }

    /// <summary>Takes back what <see cref="Preview"/> showed, the values going back to the parts' own.</summary>
    public void CancelPreview()
    {
        if (_previewing)
        {
            _previewing = false;
            _previewBy = null;
            _session.Preview(null);
        }

        Reconcile();
    }

    /// <summary>
    /// Moves the parts by <paramref name="offset"/> seconds and plays them at <paramref name="speed"/>, each kept in
    /// bounds, as one undoable step. A refusal is said in the session's status, as are notes kept once after landing on
    /// one another.
    /// </summary>
    /// <returns>Whether anything was changed.</returns>
    public bool Apply(double offset, double speed)
    {
        if (BaseSpan is null)
        {
            _session.Status = NothingToAlign;
            return false;
        }

        (offset, speed) = Bounded(offset, speed);
        if (CommandFor(offset, speed, out var leftOut) is not { } command)
        {
            Reconcile();
            return false;
        }

        _applying = true;
        bool done;
        try
        {
            done = _session.Execute(command);
        }
        finally
        {
            _applying = false;
        }

        if (done)
        {
            _states.Add((offset, speed, PrintNow()));
            SayLeftOut(leftOut);
        }

        Reconcile();
        return done;
    }

    /// <summary>Takes in a change to the chart, the focus or the tool: the parts may have changed some other way, or by undoing the tool's.</summary>
    internal void Refresh()
    {
        if (_applying)
            return;

        // A preview taken back some other way, as an undo takes back whatever's previewed, is forgotten with it.
        if (_previewing && !_session.HasPreview)
        {
            _previewing = false;
            _previewBy = null;
        }

        if (!_previewing)
        {
            Reconcile();
            return;
        }

        // Other parts picked while a change to these is still shown, as a lane is clicked before a typed speed is
        // settled: the change is kept, on the parts it was made to, and the others are taken as they are.
        if (ScopeNow() != _scope)
            Commit();
    }

    /// <summary>
    /// The tick <paramref name="tick"/> goes to, moved by <paramref name="offset"/> seconds and played at
    /// <paramref name="speed"/> from <paramref name="start"/>: worked out in the audio's time, to the nearest tick, never
    /// before the song starts.
    /// </summary>
    internal static long Map(long tick, double start, double offset, double speed, Func<double, double> secondsOf, Func<double, double> tickAt) =>
        Math.Max(0, (long)Math.Round(tickAt(start + offset + ((secondsOf(tick) - start) / speed)), MidpointRounding.AwayFromZero));

    /// <summary>
    /// <paramref name="from"/>'s notes, lyrics, phrases and star power with each tick moved by <paramref name="map"/>, put
    /// into <paramref name="into"/>, the part as it is now, so the edit is of its track. A note keeps at least a tick, if it
    /// had one, and a phrase at least a tick.
    /// </summary>
    private static VocalTrackContent Moved(VocalTrackContent from, VocalTrackContent into, Func<long, long> map)
    {
        var notes = from.Notes.Select(n =>
        {
            long tick = map(n.Tick);
            return n with { Tick = tick, Length = Math.Max(n.Length > 0 ? 1 : 0, map(n.EndTick) - tick) };
        });
        var lyrics = from.Lyrics.Select(l => l with { Tick = map(l.Tick) });
        var phrases = from.Phrases.Select(p =>
        {
            long tick = map(p.Tick);
            return p with { Tick = tick, Length = Math.Max(1, map(p.EndTick) - tick) };
        });
        var starPower = from.StarPower.Select(s =>
        {
            long tick = map(s.Tick);
            return s with { Tick = tick, Length = Math.Max(1, map(s.EndTick) - tick) };
        });
        return into.With(notes, lyrics, phrases, starPower);
    }

    /// <summary>
    /// <paramref name="from"/>'s notes, chord markings and drum mixes, each tick moved by <paramref name="map"/>, in a
    /// track of their own. A note keeps at least a tick, if it had one. A note or chord marking that lands where one is
    /// already is left out, and added to <paramref name="leftOut"/>; drum mixes may share a tick, so all are kept.
    /// </summary>
    private static (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes) Moved(
        ChartInstrument part, EditableTrack track, (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes) from, Func<long, long> map, List<RetickCollision> leftOut)
    {
        var scratch = new EditableTrack(track.Kind, track.Difficulty);
        foreach (var note in from.Notes)
        {
            var moved = ChartRetick.MoveNote(note, map, keepHeld: true);
            if (!scratch.Notes.Add(moved))
                leftOut.Add(ChartRetick.NoteLeftOut(part, track.Difficulty, note, moved));
        }

        foreach (var chord in from.Flags)
        {
            var moved = chord with { Tick = map(chord.Tick) };
            if (!scratch.FlaggedChords.Add(moved))
                leftOut.Add(ChartRetick.ChordLeftOut(part, track.Difficulty, chord, moved));
        }

        foreach (var mix in from.Mixes)
            scratch.Mixes.Add(mix with { Tick = map(mix.Tick) });
        return TrackFill.Take(scratch);
    }

    // The span moved by the offset and played at the speed.
    private static (double Start, double End) SpanOf((double Start, double End) span, double offset, double speed) =>
        (span.Start + offset, span.Start + offset + ((span.End - span.Start) / speed));

    // A map that works each tick out once.
    private static Func<long, long> Memoized(Func<long, long> map)
    {
        var ticks = new Dictionary<long, long>();
        return tick =>
        {
            if (!ticks.TryGetValue(tick, out long moved))
                ticks[tick] = moved = map(tick);
            return moved;
        };
    }

    // The offset and speed kept in bounds: the speed between half and twice, and the first note no earlier than the song's start.
    private (double Offset, double Speed) Bounded(double offset, double speed)
    {
        speed = double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1;
        offset = double.IsFinite(offset) ? offset : 0;
        if (MinOffset is { } min)
            offset = Math.Max(offset, min);
        return (offset, speed);
    }

    // The change that moves the parts, as they were taken, by the offset at the speed, made to each part as it is now;
    // null when it changes nothing. What landed where another already was, and so was left out, is in leftOut.
    private IChartCommand? CommandFor(double offset, double speed, out List<RetickCollision> leftOut)
    {
        leftOut = [];
        if (BaseSpan is not { } span)
            return null;
        var map = _session.TempoMap;
        double delay = _session.Delay.TotalSeconds;
        double SecondsOf(double tick) => map.SecondsAt(tick) + delay;
        double TickAt(double seconds) => map.TickAt(seconds - delay);

        // One map for everything moved, worked out once a tick, so items on one tick, and an item's end on another's
        // start, a phrase's ends on its notes' and so on, agree.
        var moved = Memoized(tick => Map(tick, span.Start, offset, speed, SecondsOf, TickAt));
        var commands = _baseline.CommandsFor(_session, moved, leftOut);
        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
    }

    // Which parts the focus and the options pick, whatever they hold: the vocal parts while a vocal lane has it, all or
    // the one in focus, otherwise the instrument's difficulties, all or the one in focus.
    private Scope ScopeNow()
    {
        if (_session.FocusedVocal is { } vocal)
            return new Scope(Vocal: AllVocalParts ? null : vocal, IsVocal: true, Part: null, Difficulty: null);
        if (_session.Focused is { } key)
            return new Scope(Vocal: null, IsVocal: false, Part: key.Instrument, Difficulty: AllDifficulties ? null : key.Difficulty);
        return default;
    }

    // The parts aligned as they are, by the focus, taken to be worked out from.
    private Target PartsNow()
    {
        if (_session.FocusedVocal is { } vocal)
            return new Vocals(VocalPartsOf(AllVocalParts ? Enum.GetValues<VocalTrack>() : [vocal]));
        if (_session.Focused is { } key && _session.Chart.PartOf(key.Instrument) is { } part)
            return Instrument.Of(part, AllDifficulties ? null : key.Difficulty);
        return Nothing.Instance;
    }

    // What tells the parts aligned as they are, by the focus, without taking a copy of them.
    private Print PrintNow()
    {
        if (_session.FocusedVocal is { } vocal)
            return new VocalsPrint(VocalPartsOf(AllVocalParts ? Enum.GetValues<VocalTrack>() : [vocal]));
        if (_session.Focused is { } key && _session.Chart.PartOf(key.Instrument) is { } part)
            return InstrumentPrint.Of(part, AllDifficulties ? null : key.Difficulty);
        return NothingPrint.Instance;
    }

    // The vocal parts among tracks with notes or lyrics, as they are. A vocal track is never changed, only replaced, so
    // holding on to it is no copy.
    private Dictionary<VocalTrack, VocalTrackContent> VocalPartsOf(IEnumerable<VocalTrack> tracks)
    {
        var parts = new Dictionary<VocalTrack, VocalTrackContent>();
        foreach (var track in tracks)
        {
            if (_session.VocalContentOf(track) is { } content && (content.Notes.Count > 0 || content.Lyrics.Count > 0))
                parts[track] = content;
        }

        return parts;
    }

    // The values of the step the parts are at, when they're at one; otherwise the parts as they are taken as the start
    // again, as they are when other parts are picked.
    private void Reconcile()
    {
        if (ScopeNow() == _scope)
        {
            var now = PrintNow();
            for (int i = _states.Count - 1; i >= 0; i--)
            {
                var state = _states[i];
                if (state.Parts.Matches(now))
                {
                    Show(state.Offset, state.Speed);
                    return;
                }
            }
        }

        Restart();
    }

    // Takes the parts as they are as the start: no shift, at full speed.
    private void Restart()
    {
        _scope = ScopeNow();
        _baseline = PartsNow();
        _states.Clear();
        _states.Add((0, 1, PrintNow()));
        Offset = 0;
        Speed = 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show(double offset, double speed)
    {
        Offset = offset;
        Speed = speed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Says how many notes and chord markings landed where another already was, and so were kept once.
    private void SayLeftOut(List<RetickCollision> leftOut)
    {
        int notes = leftOut.Count(static c => c.Kind == ModelDifferenceKind.Note);
        int chords = leftOut.Count - notes;
        string? said = notes switch
        {
            0 => null,
            1 => "One note landed where another already was, so it was kept once.",
            _ => FormattableString.Invariant($"{notes} notes landed where others already were, so each was kept once."),
        };
        string? marked = chords switch
        {
            0 => null,
            1 => "One chord marking landed on a chord already marked, so it was left out.",
            _ => FormattableString.Invariant($"{chords} chord markings landed on chords already marked, so they were left out."),
        };
        if (said is not null || marked is not null)
            _session.Status = said is not null && marked is not null ? $"{said} {marked}" : said ?? marked;
    }

    /// <summary>Which parts the focus and options pick: with <see cref="IsVocal"/>, the vocal parts, all or <see cref="Vocal"/>; otherwise <see cref="Part"/>'s difficulties, all or <see cref="Difficulty"/>.</summary>
    private readonly record struct Scope(VocalTrack? Vocal, bool IsVocal, ChartInstrument? Part, ChartDifficulty? Difficulty);

    /// <summary>What's aligned, as it was taken: what's moved.</summary>
    private abstract class Target
    {
        /// <summary>The first note's (or lyric's) tick and the last note's end, or null with nothing to align.</summary>
        public abstract (long First, long Last)? Ticks { get; }

        /// <summary>
        /// The changes moving what was taken by <paramref name="map"/>, made to the parts as they are now; none when
        /// nothing changes. What lands where another already is, and so is left out, is added to <paramref name="leftOut"/>.
        /// </summary>
        public abstract List<IChartCommand> CommandsFor(EditorSession session, Func<long, long> map, List<RetickCollision> leftOut);
    }

    private sealed class Nothing : Target
    {
        public static readonly Nothing Instance = new();

        public override (long First, long Last)? Ticks => null;

        public override List<IChartCommand> CommandsFor(EditorSession session, Func<long, long> map, List<RetickCollision> leftOut) => [];
    }

    /// <summary>What tells whether the parts are as they were, cheap to keep for each step and to compare.</summary>
    private abstract record Print
    {
        /// <summary>Whether <paramref name="other"/> is the same parts, holding the same, as this.</summary>
        public virtual bool Matches(Print other) => Equals(other);
    }

    private sealed record NothingPrint : Print
    {
        public static readonly NothingPrint Instance = new();
    }

    /// <summary>The vocal parts, by the tracks they were read from: a vocal track is never changed, only replaced, so the one it was read from tells.</summary>
    private sealed record VocalsPrint(Dictionary<VocalTrack, VocalTrackContent> Parts) : Print
    {
        public override bool Matches(Print other)
        {
            if (other is not VocalsPrint vocals)
                return false;
            var mine = Read(this);
            var theirs = Read(vocals);
            return mine.Count == theirs.Count && mine.All(p => theirs.TryGetValue(p.Key, out var track) && ReferenceEquals(track, p.Value));

            static Dictionary<VocalTrack, PassthroughTrack> Read(VocalsPrint vocals) =>
                vocals.Parts.Where(static p => p.Value.Source is not null).ToDictionary(static p => p.Key, static p => p.Value.Source!);
        }
    }

    /// <summary>
    /// An instrument's difficulties, all or <see cref="Only"/>, by a hash of what each holds and, all aligned, of what
    /// they share: an instrument's lists are changed in place, so there's no track to hold on to, and a copy of each
    /// step's would be a copy of the whole instrument.
    /// </summary>
    private sealed record InstrumentPrint(ChartInstrument Part, ChartDifficulty? Only, int Tracks, ulong Hash) : Print
    {
        /// <summary><paramref name="part"/> as it is, picked as <see cref="Instrument.Of"/> picks it.</summary>
        public static InstrumentPrint Of(EditablePart part, ChartDifficulty? only)
        {
            var hash = new Fingerprint();
            int tracks = 0;
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count == 0 || (only is not null && track.Difficulty != only))
                    continue;
                tracks++;
                hash.Add((int)track.Difficulty);
                hash.Add(track.Notes.AsSpan());
                hash.Add(track.FlaggedChords.AsSpan());
                hash.Add(track.Mixes.AsSpan());
            }

            if (only is null && tracks > 0)
            {
                hash.Add(part.Phrases.AsSpan());
                hash.Add(part.TomMarkers.AsSpan());
            }

            return new InstrumentPrint(part.Instrument, only, tracks, hash.Value);
        }
    }

    /// <summary>A 64-bit FNV-1a hash of items' own hashes, and of each list's length.</summary>
    private struct Fingerprint()
    {
        public ulong Value { get; private set; } = 14695981039346656037;

        public void Add(int value) => Value = (Value ^ (uint)value) * 1099511628211;

        public void Add<T>(ReadOnlySpan<T> items)
        {
            Add(items.Length);
            foreach (var item in items)
                Add(EqualityComparer<T>.Default.GetHashCode(item!));
        }
    }

    /// <summary>The vocal parts with notes or lyrics, each as its track had it.</summary>
    private sealed class Vocals(Dictionary<VocalTrack, VocalTrackContent> parts) : Target
    {
        public Dictionary<VocalTrack, VocalTrackContent> Parts { get; } = parts;

        public override (long First, long Last)? Ticks
        {
            get
            {
                long? first = null, last = null;
                foreach (var content in Parts.Values)
                {
                    foreach (var note in content.Notes)
                    {
                        first = Math.Min(first ?? note.Tick, note.Tick);
                        last = Math.Max(last ?? note.EndTick, note.EndTick);
                    }

                    foreach (var lyric in content.Lyrics)
                    {
                        first = Math.Min(first ?? lyric.Tick, lyric.Tick);
                        last = Math.Max(last ?? lyric.Tick, lyric.Tick);
                    }
                }

                return first is { } from && last is { } to ? (from, to) : null;
            }
        }

        public override List<IChartCommand> CommandsFor(EditorSession session, Func<long, long> map, List<RetickCollision> leftOut)
        {
            var commands = new List<IChartCommand>();
            foreach (var (track, from) in Parts.OrderBy(static p => p.Key))
            {
                if (session.VocalContentOf(track) is not { } now)
                    continue;
                var to = Moved(from, now, map);
                if (!ReferenceEquals(to, now))
                    commands.Add(new ChangeVocals(track, to));
            }

            return commands;
        }
    }

    /// <summary>
    /// An instrument's difficulties with notes, each as its track had it, and, when every difficulty is aligned, the
    /// phrases and tom markers they share.
    /// </summary>
    private sealed class Instrument(
        ChartInstrument part,
        Dictionary<ChartDifficulty, (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes)> tracks,
        Phrase[]? phrases,
        TomMarker[]? tomMarkers) : Target
    {
        public ChartInstrument Part { get; } = part;

        public Dictionary<ChartDifficulty, (EditableNote[] Notes, FlaggedChord[] Flags, DrumMix[] Mixes)> Tracks { get; } = tracks;

        /// <summary>The phrases every difficulty shares, or null when they're not aligned.</summary>
        public Phrase[]? Phrases { get; } = phrases;

        /// <summary>The tom markers every difficulty shares, or null when they're not aligned.</summary>
        public TomMarker[]? TomMarkers { get; } = tomMarkers;

        /// <summary><paramref name="part"/> as it is: every difficulty with notes and what they share, or <paramref name="only"/> alone.</summary>
        public static Instrument Of(EditablePart part, ChartDifficulty? only)
        {
            var tracks = new Dictionary<ChartDifficulty, (EditableNote[], FlaggedChord[], DrumMix[])>();
            foreach (var track in part.Tracks)
            {
                if (track.Notes.Count > 0 && (only is null || track.Difficulty == only))
                    tracks[track.Difficulty] = TrackFill.Take(track);
            }

            bool shared = only is null && tracks.Count > 0;
            return new Instrument(
                part.Instrument,
                tracks,
                shared ? part.Phrases.AsSpan().ToArray() : null,
                shared ? part.TomMarkers.AsSpan().ToArray() : null);
        }

        public override (long First, long Last)? Ticks
        {
            get
            {
                long? first = null, last = null;
                foreach (var (notes, _, _) in Tracks.Values)
                {
                    foreach (var note in notes)
                    {
                        first = Math.Min(first ?? note.Tick, note.Tick);
                        last = Math.Max(last ?? note.Tick + note.Length, note.Tick + note.Length);
                    }
                }

                return first is { } from && last is { } to ? (from, to) : null;
            }
        }

        public override List<IChartCommand> CommandsFor(EditorSession session, Func<long, long> map, List<RetickCollision> leftOut)
        {
            var commands = new List<IChartCommand>();
            if (session.Chart.PartOf(Part) is not { } part)
                return commands;

            foreach (var (difficulty, from) in Tracks.OrderBy(static p => p.Key))
            {
                var track = part[difficulty];
                var to = Moved(Part, track, from, map, leftOut);
                if (!to.Notes.AsSpan().SequenceEqual(track.Notes.AsSpan())
                    || !to.Flags.AsSpan().SequenceEqual(track.FlaggedChords.AsSpan())
                    || !to.Mixes.AsSpan().SequenceEqual(track.Mixes.AsSpan()))
                {
                    commands.Add(new ReplaceTrack(Part, difficulty, to.Notes, to.Flags, to.Mixes.Length > 0 || track.Mixes.Count > 0 ? to.Mixes : null));
                }
            }

            // Each keeps at least a tick, if it had one, as the notes do.
            if (Phrases is not null && TomMarkers is not null)
            {
                Phrase[] phrases = [.. Phrases.Select(p => ChartRetick.MovePhrase(p, map, keepHeld: true))];
                TomMarker[] toms = [.. TomMarkers.Select(m => ChartRetick.MoveTomMarker(m, map, keepHeld: true))];
                if (!phrases.AsSpan().SequenceEqual(part.Phrases.AsSpan()) || !toms.AsSpan().SequenceEqual(part.TomMarkers.AsSpan()))
                    commands.Add(new ReplacePartSpans(Part, phrases, toms));
            }

            return commands;
        }
    }
}
