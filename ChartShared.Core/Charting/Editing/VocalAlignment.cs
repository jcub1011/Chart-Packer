using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>What a press of the Vocal Alignment tool takes hold of: the start's handle, the end's, or the notes between.</summary>
public enum VocalAlignGrip
{
    /// <summary>The notes, to shift them all in time.</summary>
    Body,

    /// <summary>The handle before the first note, to stretch the notes with the last one's end kept where it is.</summary>
    Start,

    /// <summary>The handle after the last note's end, to stretch the notes with the first one kept where it is.</summary>
    End,
}

/// <summary>
/// The Vocal Alignment tool's shift and stretch of the vocal parts (charter-spec C3 item 7), for lyrics timed to other
/// audio: the session owns it (<see cref="EditorSession.VocalAlignment"/>). Every vocal part, or only the one in focus,
/// is moved in time by <see cref="Offset"/> and played at <see cref="Speed"/>, as one undoable step each time they change.
/// </summary>
/// <remarks>
/// <para>The parts are aligned in the audio's time, not in ticks, so a tempo map that changes doesn't bend them: a time
/// <c>t</c> goes to <c>start + Offset + (t − start) / Speed</c>, where <c>start</c> is when the first note or lyric was,
/// and each note's start and end, each lyric, each phrase's ends and each star power phrase's ends are put on the tick
/// nearest their new time, with no snapping, and never before the song starts. Everything else in a part's track is
/// left where it is.</para>
/// <para><see cref="Offset"/> and <see cref="Speed"/> are kept from the parts as they were when the tool was first used
/// on them: each change is worked out from those parts again, not from the last change, so typing a speed means that
/// speed of the parts as they were, and rounding to ticks doesn't build up. They're forgotten, and the parts as they are
/// taken as the start again, once the parts are changed some other way; undoing and redoing the tool's own changes keeps
/// them, each step's values coming back with it.</para>
/// </remarks>
public sealed class VocalAlignment
{
    /// <summary>The slowest the parts may be played: half speed, twice as long.</summary>
    public const double MinSpeed = 0.5;

    /// <summary>The fastest the parts may be played: twice speed, half as long.</summary>
    public const double MaxSpeed = 2.0;

    private const string NothingToAlign = "There are no vocal notes or lyrics to align.";

    private readonly EditorSession _session;

    // The parts as they were when the values were last taken from nothing, which every change is worked out from.
    private Dictionary<VocalTrack, VocalTrackContent> _baseline = [];

    // The values each change made, with the tracks it left, so undoing or redoing one brings its values back. The first is
    // the baseline's, no shift at full speed.
    private readonly List<(double Offset, double Speed, Dictionary<VocalTrack, PassthroughTrack> Tracks)> _states = [];

    // Set while the tool's own change is being made, so the change isn't taken for one made some other way.
    private bool _applying;
    private bool _previewing;

    internal VocalAlignment(EditorSession session) => _session = session;

    /// <summary>The offset or speed changed, or the parts aligned did: what the tool shows is to be drawn again.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether every vocal part is aligned together, as it is by default, rather than only the one in focus.</summary>
    public bool AllParts
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

    /// <summary>The vocal parts being aligned, in order.</summary>
    public IReadOnlyList<VocalTrack> Parts => [.. _baseline.Keys.Order()];

    /// <summary>Whether <paramref name="track"/> is one of the parts being aligned.</summary>
    public bool Aligns(VocalTrack track) => _baseline.ContainsKey(track);

    /// <summary>
    /// When the parts' first note or lyric was, and their last note's end, in seconds of audio, before the offset and
    /// speed: what <see cref="Span"/> is worked out from. Null with nothing to align.
    /// </summary>
    public (double Start, double End)? BaseSpan
    {
        get
        {
            long? first = null, last = null;
            foreach (var content in _baseline.Values)
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

            if (first is not { } from || last is not { } to)
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
    public VocalAlignGrip? GripAt(double tick, double tolerance)
    {
        if (Span is not { } span)
            return null;
        double from = _session.TickAt(span.Start), to = _session.TickAt(span.End);
        if (tick >= from - (3 * tolerance) && tick <= from + tolerance && tick < (from + to) / 2)
            return VocalAlignGrip.Start;
        if (tick >= to - tolerance && tick <= to + (3 * tolerance))
            return VocalAlignGrip.End;
        return VocalAlignGrip.Body;
    }

    /// <summary>
    /// Shows the parts moved by <paramref name="offset"/> seconds and played at <paramref name="speed"/>, each kept in
    /// bounds, without putting it on the undo stack: as a drag or a scrubbed field goes on. <see cref="Commit"/> keeps it.
    /// </summary>
    /// <returns>Whether it could be shown.</returns>
    public bool Preview(double offset, double speed)
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
        if (CommandFor(offset, speed) is not { } command)
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
        Show(offset, speed);
        return true;
    }

    /// <summary>Puts what <see cref="Preview"/> showed on the undo stack as one step, as a drag ends or a field is settled.</summary>
    /// <returns>Whether anything was changed.</returns>
    public bool Commit()
    {
        double offset = Offset, speed = Speed;
        bool previewing = _previewing;
        _previewing = false;
        if (previewing)
            _session.Preview(null);
        return Apply(offset, speed);
    }

    /// <summary>Takes back what <see cref="Preview"/> showed, the values going back to the parts' own.</summary>
    public void CancelPreview()
    {
        if (_previewing)
        {
            _previewing = false;
            _session.Preview(null);
        }

        Reconcile();
    }

    /// <summary>
    /// Moves the parts by <paramref name="offset"/> seconds and plays them at <paramref name="speed"/>, each kept in
    /// bounds, as one undoable step. A refusal is said in the session's status.
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
        if (CommandFor(offset, speed) is not { } command)
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
            _states.Add((offset, speed, TracksNow()));
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
            _previewing = false;
        if (!_previewing)
            Reconcile();
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
    internal static VocalTrackContent Moved(VocalTrackContent from, VocalTrackContent into, Func<long, long> map)
    {
        var ticks = new Dictionary<long, long>();
        long At(long tick)
        {
            if (!ticks.TryGetValue(tick, out long moved))
                ticks[tick] = moved = map(tick);
            return moved;
        }

        var notes = from.Notes.Select(n =>
        {
            long tick = At(n.Tick);
            return n with { Tick = tick, Length = Math.Max(n.Length > 0 ? 1 : 0, At(n.EndTick) - tick) };
        });
        var lyrics = from.Lyrics.Select(l => l with { Tick = At(l.Tick) });
        var phrases = from.Phrases.Select(p =>
        {
            long tick = At(p.Tick);
            return p with { Tick = tick, Length = Math.Max(1, At(p.EndTick) - tick) };
        });
        var starPower = from.StarPower.Select(s =>
        {
            long tick = At(s.Tick);
            return s with { Tick = tick, Length = Math.Max(1, At(s.EndTick) - tick) };
        });
        return into.With(notes, lyrics, phrases, starPower);
    }

    // The span moved by the offset and played at the speed.
    private static (double Start, double End) SpanOf((double Start, double End) span, double offset, double speed) =>
        (span.Start + offset, span.Start + offset + ((span.End - span.Start) / speed));

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
    // null when it changes nothing.
    private IChartCommand? CommandFor(double offset, double speed)
    {
        if (BaseSpan is not { } span)
            return null;
        var map = _session.TempoMap;
        double delay = _session.Delay.TotalSeconds;
        double SecondsOf(double tick) => map.SecondsAt(tick) + delay;
        double TickAt(double seconds) => map.TickAt(seconds - delay);

        var commands = new List<IChartCommand>();
        foreach (var (track, from) in _baseline.OrderBy(static p => p.Key))
        {
            if (_session.VocalContentOf(track) is not { } now)
                continue;
            var to = Moved(from, now, tick => Map(tick, span.Start, offset, speed, SecondsOf, TickAt));
            if (!ReferenceEquals(to, now))
                commands.Add(new ChangeVocals(track, to));
        }

        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands),
        };
    }

    // The parts aligned as they are: every one with notes or lyrics, or only the one in focus.
    private Dictionary<VocalTrack, VocalTrackContent> PartsNow()
    {
        var parts = new Dictionary<VocalTrack, VocalTrackContent>();
        foreach (var track in AllParts ? Enum.GetValues<VocalTrack>() : _session.FocusedVocal is { } focused ? [focused] : [])
        {
            if (_session.VocalContentOf(track) is { } content && (content.Notes.Count > 0 || content.Lyrics.Count > 0))
                parts[track] = content;
        }

        return parts;
    }

    private Dictionary<VocalTrack, PassthroughTrack> TracksNow() =>
        PartsNow().Where(static p => p.Value.Source is not null).ToDictionary(static p => p.Key, static p => p.Value.Source!);

    // The values of the step the parts are at, when they're at one; otherwise the parts as they are taken as the start again.
    private void Reconcile()
    {
        var tracks = TracksNow();
        for (int i = _states.Count - 1; i >= 0; i--)
        {
            var state = _states[i];
            if (state.Tracks.Count == tracks.Count && state.Tracks.All(p => tracks.TryGetValue(p.Key, out var track) && ReferenceEquals(track, p.Value)))
            {
                Show(state.Offset, state.Speed);
                return;
            }
        }

        Restart();
    }

    // Takes the parts as they are as the start: no shift, at full speed.
    private void Restart()
    {
        _baseline = PartsNow();
        _states.Clear();
        _states.Add((0, 1, TracksNow()));
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
}
