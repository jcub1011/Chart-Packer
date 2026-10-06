using System.Globalization;
using ChartPacker.Core.Charts;
using Serilog;

namespace ChartPacker.Core.Charting;

/// <summary>
/// Reads a <c>notes.mid</c>, or a <c>notes.chart</c> converted to one, into the editable model (charter-spec §4.1),
/// following <see cref="MidiChartReader"/>'s YARG rules for which tracks are read, how notes pair and which of two
/// notes is kept, so that the model plays as the file did.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The tempo track gives the song's name, the tempo and time-signature markers at the ticks YARG reads them at,
/// and the anchors; its other events are kept as raw events.</item>
/// <item><c>EVENTS</c> gives the global text events; its other events are kept as raw events.</item>
/// <item>Each part track <see cref="MidiChartReader"/> reads gives a part, but <c>PART KEYS</c>, which v1 doesn't edit
/// (§7.4). What the model holds elsewhere (notes, markers, phrases and the part's settings) is taken from it; what the
/// games don't read and could start reading once rewritten is left out, with a <see cref="ChartLoadIssue"/>; and the
/// rest is kept as text and raw events.</item>
/// <item><c>BEAT</c> is left out, as it's made again from the tempo map on every save (§4.2). Every other chunk is
/// kept byte for byte, in its order.</item>
/// </list>
/// </remarks>
public static class MidiChartLoader
{
    // The events of the tempo track that anchor a tempo marker to the audio (§7.3).
    private const string AnchorStart = "[chartpacker_anchor ";
    private const string AnchorEnd = "]";

    // How many issues the summary logged names.
    private const int IssuesNamed = 5;

    private static ILogger Logger => Log.ForContext(typeof(MidiChartLoader));

    /// <summary>Reads the <c>notes.mid</c> <paramref name="midi"/> into the editable model.</summary>
    /// <param name="options">The song's settings, as <see cref="ChartReadOptions.From"/> reads them.</param>
    /// <exception cref="ChartFormatException">The file is one <see cref="MidiChartReader"/> can't read.</exception>
    public static ChartLoadResult Load(byte[] midi, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(midi);
        ArgumentNullException.ThrowIfNull(options);

        // As the reader reads the file, so it fails where the reader does.
        var (resolution, chunks) = MidiChartReader.ChunksOf(midi);
        var tempoEvents = MidiFile.ReadEvents(midi, chunks[0]);
        var parts = MidiChartReader.PartTracksOf(midi, chunks);

        var chart = new EditableChart(resolution);
        var issues = new List<ChartLoadIssue>();
        LoadTempoTrack(midi, tempoEvents, chart, issues);

        // Keys pass through: v1 doesn't edit them.
        var edited = parts.Where(p => p.Key != ChartInstrument.Keys).ToDictionary(p => p.Value.Chunk, p => p.Key);
        int? events = null;
        for (int i = 1; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (edited.ContainsKey(i))
                continue;
            if (chunk.Id != "MTrk")
            {
                chart.AddPassthrough(new PassthroughTrack(midi[chunk.Start..chunk.End], null));
                continue;
            }

            string? name = MidiChartReader.NameOf(midi, chunk);
            if (name == "BEAT")
                continue;
            if (name == "EVENTS" && events is null)
            {
                events = i;
                continue;
            }

            chart.AddPassthrough(new PassthroughTrack(midi[chunk.Start..chunk.End], MidiFile.NameOf(midi, chunk)));
        }

        long? coda = events is { } at ? LoadEvents(midi, MidiFile.ReadEvents(midi, chunks[at]), chart) : null;
        foreach (var (instrument, part) in parts.Where(p => p.Key != ChartInstrument.Keys).OrderBy(p => p.Key))
            new PartLoader(midi, part.Events, chart.AddPart(instrument), options, issues).Load(part.Name, coda);

        if (parts.TryGetValue(ChartInstrument.Drums, out var drums))
        {
            chart.DrumsVariant = MidiChartReader.DrumsLayoutOf(midi, drums.Events, options) switch
            {
                DrumsLayout.FourLane => DrumsVariant.FourLane,
                DrumsLayout.FiveLane => DrumsVariant.FiveLane,
                _ => DrumsVariant.Pro,
            };
        }

        LogIssues(chart, issues);
        return new ChartLoadResult(chart, issues);
    }

    /// <summary>
    /// Converts the <c>notes.chart</c> <paramref name="chartBytes"/> with <see cref="ChartToMidi"/>, and reads the result
    /// as <see cref="Load"/> does, with the settings the conversion reads it with.
    /// </summary>
    /// <param name="options">The song's settings, as <see cref="ChartReadOptions.From"/> reads them.</param>
    /// <exception cref="ChartFormatException">The chart is malformed, or can't be written as MIDI.</exception>
    public static ChartImportResult LoadChart(byte[] chartBytes, ChartReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(chartBytes);
        ArgumentNullException.ThrowIfNull(options);
        var conversion = ChartToMidi.Convert(chartBytes, options);
        return new ChartImportResult(Load(conversion.Midi, conversion.Options), conversion.Offset, conversion.Options, conversion.Warnings);
    }

    /// <summary>
    /// The tempo track: its name, its tempo and time-signature markers at the ticks YARG reads them at, its anchors at
    /// their markers', and the rest as raw events where they are. The end of the track is left to the writer.
    /// </summary>
    private static void LoadTempoTrack(byte[] bytes, List<MidiEvent> events, EditableChart chart, List<ChartLoadIssue> issues)
    {
        var map = chart.TempoMap;
        var taken = new bool[events.Count];
        var unholdable = new Dictionary<int, ChartLoadIssueKind>();
        foreach (var e in MidiChartReader.SyncEventsOf(bytes, events))
        {
            if (e.IsTempo && e.MicrosecondsPerQuarter >= 1)
                map.Tempos.Add(new TempoChange(e.Tick, e.MicrosecondsPerQuarter));
            else if (!e.IsTempo && e.Numerator >= 1)
                map.TimeSignatures.Add(new TimeSignatureMarker(e.Tick, e.Numerator, 1 << e.DenominatorPower, e.ClocksPerClick, e.ThirtySecondsPerQuarter));
            else
                unholdable[e.Index] = e.IsTempo ? ChartLoadIssueKind.UnholdableTempo : ChartLoadIssueKind.UnholdableTimeSignature;
            taken[e.Index] = !unholdable.ContainsKey(e.Index);
        }

        // An anchor is written at its marker's tick, so it moves with it by the first event's delta too.
        long shift = events.Count > 0 ? events[0].Tick : 0;
        bool named = false;
        for (int i = 0; i < events.Count; i++)
        {
            if (taken[i])
                continue;
            var e = events[i];
            if (unholdable.TryGetValue(i, out var kind))
            {
                issues.Add(new ChartLoadIssue(kind, null, null, e.Tick));
            }
            else if (e.IsMeta)
            {
                var (type, start, length) = MidiFile.MetaOf(bytes, e);
                if (type == MidiFile.EndOfTrack)
                    continue;
                if (type == 0x03 && !named)
                {
                    chart.Name = MidiFile.DecodeText(bytes.AsSpan(start, length));
                    named = true;
                    continue;
                }

                if (type == TextEvent.TextType && AnchorOf(MidiFile.DecodeText(bytes.AsSpan(start, length))) is { } microseconds)
                {
                    long tick = e.Tick + shift;
                    if (!map.Tempos.At(tick).IsEmpty && map.Anchors.Add(new TempoAnchor(tick, microseconds)))
                        continue;
                    issues.Add(new ChartLoadIssue(ChartLoadIssueKind.StrayAnchor, null, null, e.Tick));
                }
            }

            chart.TempoTrackRaw.Add(RawOf(bytes, e));
        }
    }

    /// <summary>
    /// <c>EVENTS</c>: its text events, and the rest as raw events. Its name and the end of the track are left to the
    /// writer.
    /// </summary>
    /// <returns>The tick of its first <c>[coda]</c>, if it has one.</returns>
    private static long? LoadEvents(byte[] bytes, List<MidiEvent> events, EditableChart chart)
    {
        long? coda = null;
        bool named = false;
        foreach (var e in events)
        {
            if (e.IsMeta)
            {
                var (type, start, length) = MidiFile.MetaOf(bytes, e);
                if (type == MidiFile.EndOfTrack)
                    continue;
                if (type == 0x03 && !named && e.Tick == 0 && MidiFile.DecodeText(bytes.AsSpan(start, length)) == "EVENTS")
                {
                    named = true;
                    continue;
                }

                if (IsText(type))
                {
                    string text = MidiFile.DecodeText(bytes.AsSpan(start, length));
                    chart.Events.Add(new TextEvent(e.Tick, (byte)type, text));
                    if (coda is null && IsReadText(type) && ChartTextEvents.Normalize(text) == "coda")
                        coda = e.Tick;
                    continue;
                }
            }

            chart.EventsRaw.Add(RawOf(bytes, e));
        }

        return coda;
    }

    /// <summary>The audio time an anchor event gives, written exactly as the writer writes one, so it's written back the same.</summary>
    private static long? AnchorOf(string text)
    {
        if (!text.StartsWith(AnchorStart, StringComparison.Ordinal) || !text.EndsWith(AnchorEnd, StringComparison.Ordinal) || text.Length <= AnchorStart.Length + AnchorEnd.Length)
            return null;
        string number = text[AnchorStart.Length..^AnchorEnd.Length];
        return long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long microseconds)
            && number == microseconds.ToString(CultureInfo.InvariantCulture)
                ? microseconds
                : null;
    }

    /// <summary>The anchor event's text for an anchor at <paramref name="microseconds"/> of audio, as <see cref="AnchorOf"/> reads it.</summary>
    internal static string AnchorTextOf(long microseconds) =>
        AnchorStart + microseconds.ToString(CultureInfo.InvariantCulture) + AnchorEnd;

    /// <summary>Whether a meta event of <paramref name="type"/> is a text event: any text type but the track name.</summary>
    internal static bool IsText(int type) => type is >= 0x01 and <= 0x09 and not 0x03;

    /// <summary>Whether a meta event of <paramref name="type"/> is a text event YARG reads, as <see cref="MidiTrackNotes"/> takes them.</summary>
    internal static bool IsReadText(int type) => type is 0x01 or (>= 0x04 and <= 0x09);

    private static RawEvent RawOf(byte[] bytes, MidiEvent e) => new(e.Tick, MidiMessage.Of(bytes, e).Bytes);

    private static void LogIssues(EditableChart chart, List<ChartLoadIssue> issues)
    {
        if (issues.Count == 0)
            return;
        string first = string.Join(", ", issues.Take(IssuesNamed).Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Kind} at {i.Tick}")));
        Logger.Warning("Loading the chart {Name} left out or kept raw {Count} events: {First}", chart.Name, issues.Count, first);
        foreach (var issue in issues)
            Logger.Debug("Load issue {Kind} on {Part} {Difficulty} at tick {Tick}, pitch {Pitch}", issue.Kind, issue.Part, issue.Difficulty, issue.Tick, issue.Pitch);
    }

    /// <summary>A note that can be one of a track's notes, and the lane, pad or kick it'd be in.</summary>
    /// <param name="Lane">A fret note's lane; a drum note's pad, -1 its double kick.</param>
    private readonly record struct Candidate(MidiNote Note, int Lane);

    /// <summary>A marker or Phase Shift phrase, and whether it covers any note.</summary>
    private sealed class Marker(TickSpan span, int pitch, int? difficulty)
    {
        public TickSpan Span { get; } = span;

        /// <summary>The marker's pitch, or the Phase Shift phrase's code.</summary>
        public int Pitch { get; } = pitch;

        /// <summary>The Phase Shift phrase's difficulty, 0 to 3 or <see cref="MidiLayout.SysexAllDifficulties"/>; null for a note.</summary>
        public int? Difficulty { get; } = difficulty;

        public bool Used { get; set; }
    }

    /// <summary>Reads one part track into its <see cref="EditablePart"/>.</summary>
    private sealed class PartLoader(byte[] bytes, List<MidiEvent> events, EditablePart part, ChartReadOptions options, List<ChartLoadIssue> issues)
    {
        private readonly byte[] _bytes = bytes;
        private readonly List<MidiEvent> _events = events;
        private readonly EditablePart _part = part;
        private readonly ChartReadOptions _options = options;
        private readonly List<ChartLoadIssue> _issues = issues;
        private readonly MidiTrackNotes _track = MidiTrackNotes.Of(bytes, events, part.Kind == ChartInstrumentKind.FiveFret);

        // The events the model has taken, or left out: the rest are kept as text and raw events.
        private readonly bool[] _taken = new bool[events.Count];

        private ChartInstrumentKind Kind => _part.Kind;

        private bool IsDrums => Kind == ChartInstrumentKind.Drums;

        /// <param name="name">The track's name, as the reader took it.</param>
        /// <param name="coda">The tick of <c>EVENTS</c>' first <c>[coda]</c>, after which drum fills are big rock endings.</param>
        public void Load(string name, long? coda)
        {
            Name(name);
            if (IsDrums)
                Drums();
            else
                Frets();
            Phrases(coda);
            Texts();
            Unpaired();
            Rest();
        }

        /// <summary>
        /// The track's name, which the writer writes. The first event, which YARG never reads, is the name or is left
        /// out: written after the name, it would start being read.
        /// </summary>
        private void Name(string name)
        {
            if (_events.Count == 0)
                return;
            string? first = TextOf(_events[0], 0x03);
            if (first is null)
                Issue(ChartLoadIssueKind.FirstEventDropped, null, _events[0].Tick, PitchOf(_events[0]));
            _taken[0] = true;
            if (first == name)
                return;

            // The name the reader took is another track-name event at tick 0.
            for (int i = 1; i < _events.Count && _events[i].Tick == 0; i++)
            {
                if (TextOf(_events[i], 0x03) == name)
                {
                    _taken[i] = true;
                    return;
                }
            }
        }

        /// <summary>Each difficulty's fret notes, and the force and tap markers over them, which mark their chords.</summary>
        private void Frets()
        {
            var sysex = PhaseShiftMarkers();
            var taps = MarkersOn(MidiLayout.TapPitch);
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                int b = MidiLayout.BaseOf(Kind, difficulty);
                var gems = new List<Candidate>();
                foreach (var note in _track.Notes)
                {
                    if (MidiLayout.DifficultyOf(Kind, note.Pitch) != difficulty)
                        continue;
                    int offset = note.Pitch - b;
                    if (MidiLayout.FretLaneOf(Kind, offset, note.EnhancedOpens) is { } lane)
                    {
                        gems.Add(new Candidate(note, lane));
                    }
                    else if (offset == -1)
                    {
                        Take(note);
                        Issue(ChartLoadIssueKind.OpenBeforeEnhancedOpens, difficulty, note.Start, note.Pitch);
                    }
                }

                // As the reader: the first of each lane at a tick is kept, then Phase Shift open phrases make the notes
                // they cover open, which the model can hold only one of at a tick.
                var track = _part[difficulty];
                var opens = For(sysex, difficulty, MidiLayout.SysexOpen);
                foreach (var gem in KeepFirst(gems, difficulty, static c => c.Lane))
                {
                    var note = gem.Note;
                    int lane = Cover(opens, note.Start) ? ChartNote.OpenLane : gem.Lane;
                    if (!track.Notes.Add(new EditableNote(note.Start, note.End - note.Start, lane)))
                        Issue(ChartLoadIssueKind.DuplicateOpen, difficulty, note.Start, note.Pitch);
                    else if (lane == ChartNote.OpenLane && Kind == ChartInstrumentKind.FiveFret)
                        _part.EnhancedOpens = true; // Opens are written below green, which needs the event.
                }

                var forceHopo = MarkersOn(b + MidiLayout.ForceHopoOffset(Kind));
                var forceStrum = MarkersOn(b + MidiLayout.ForceStrumOffset(Kind));
                var sysexTaps = For(sysex, difficulty, MidiLayout.SysexTap);
                long? last = null;
                foreach (var note in track.Notes)
                {
                    if (note.Tick == last)
                        continue;
                    last = note.Tick;
                    var flags = (Cover(forceHopo, note.Tick) ? ChordFlags.ForceHopo : ChordFlags.None)
                        | (Cover(forceStrum, note.Tick) ? ChordFlags.ForceStrum : ChordFlags.None)
                        | (Cover(taps, note.Tick) | Cover(sysexTaps, note.Tick) ? ChordFlags.Tap : ChordFlags.None);
                    if (flags != ChordFlags.None)
                        track.SetFlags(note.Tick, flags);
                }

                Unused(forceHopo, difficulty);
                Unused(forceStrum, difficulty);
            }

            Unused(taps, null);
            foreach (var marker in sysex)
            {
                if (!marker.Used)
                    Issue(ChartLoadIssueKind.UnusedMarker, marker.Difficulty is { } d and <= 3 ? (ChartDifficulty)d : null, marker.Span.Start, null);
            }
        }

        /// <summary>Each difficulty's drum notes, with their accents and ghosts once the track has <c>[ENABLE_CHART_DYNAMICS]</c>.</summary>
        private void Drums()
        {
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                int b = MidiLayout.BaseOf(ChartInstrumentKind.Drums, difficulty);
                var hits = new List<Candidate>();
                foreach (var note in _track.Notes)
                {
                    // As the reader has them: the pitch below an easier difficulty's kick reads as its double kick too,
                    // which the games drop, though it's kept over a kick it comes before.
                    if (MidiLayout.DrumPadOf(note.Pitch - b) is { } pad)
                        hits.Add(new Candidate(note, pad));
                }

                var track = _part[difficulty];
                foreach (var hit in KeepFirst(hits, difficulty, static c => Math.Max(0, c.Lane), IsPhantom))
                {
                    if (IsPhantom(hit))
                        continue;
                    var note = hit.Note;
                    var dynamics = _track.HasChartDynamics(note) ? DynamicsOf(note.Velocity) : NoteDynamics.None;
                    track.Notes.Add(new EditableNote(note.Start, note.End - note.Start, Math.Max(0, hit.Lane), dynamics, DoubleKick: hit.Lane == -1));
                }

                bool IsPhantom(Candidate hit) => hit.Lane == -1 && difficulty != ChartDifficulty.Expert;
            }

            foreach (var note in _track.Notes)
            {
                if (note.Pitch is >= MidiLayout.FirstTomPitch and <= MidiLayout.FirstTomPitch + 2)
                {
                    Take(note);
                    _part.TomMarkers.Add(new TomMarker(note.Pitch - MidiLayout.FirstTomPitch + TomMarker.FirstPad, note.Start, note.End - note.Start));
                }
                else if (note.Pitch is MidiLayout.RollPitch or MidiLayout.TrillPitch)
                {
                    Take(note);
                    var kind = note.Pitch == MidiLayout.RollPitch ? PhraseKind.Roll : PhraseKind.Trill;
                    _part.Phrases.Add(new Phrase(kind, note.Start, note.End - note.Start, LowestDifficultyOf(note.Velocity)));
                }
            }
        }

        /// <summary>
        /// Star power, solos, and the drum fills and big rock endings that hold all five of their pitches together.
        /// </summary>
        private void Phrases(long? coda)
        {
            int starPower = _options.MidiStarPowerPitch;
            var starts = new HashSet<long>();
            foreach (var note in TickOrder.Sorted(NotesOn(starPower), static n => n.Start))
            {
                Take(note);
                if (starts.Add(note.Start))
                    _part.Phrases.Add(new Phrase(PhraseKind.StarPower, note.Start, note.End - note.Start));
                else
                    Issue(ChartLoadIssueKind.DuplicateStarPower, null, note.Start, note.Pitch);
            }

            foreach (var note in NotesOn(starPower == MidiLayout.StarPowerPitch ? MidiLayout.SoloPitch : MidiLayout.StarPowerPitch))
            {
                Take(note);
                if (starPower == MidiLayout.StarPowerPitch)
                    _part.Phrases.Add(new Phrase(PhraseKind.Solo, note.Start, note.End - note.Start));
                else
                    Issue(ChartLoadIssueKind.UnreadStarPower, null, note.Start, note.Pitch);
            }

            // A phrase holds all five pitches from one tick to another; any other note on them is kept as it is.
            var lanes = Enumerable.Range(MidiLayout.DrumFillPitch, 5).Select(p => NotesOn(p, take: false)).ToArray();
            var used = new HashSet<int>();
            foreach (var first in lanes[0])
            {
                var set = new List<MidiNote>(5) { first };
                for (int lane = 1; lane < lanes.Length; lane++)
                {
                    foreach (var note in lanes[lane])
                    {
                        if (note.Start == first.Start && note.End == first.End && !used.Contains(note.OnIndex))
                        {
                            set.Add(note);
                            break;
                        }
                    }
                }

                if (set.Count < lanes.Length)
                    continue;
                foreach (var note in set)
                {
                    used.Add(note.OnIndex);
                    Take(note);
                }

                var kind = !IsDrums || first.Start >= coda ? PhraseKind.BigRockEnding : PhraseKind.DrumFill;
                _part.Phrases.Add(new Phrase(kind, first.Start, first.End - first.Start));
            }
        }

        /// <summary>
        /// The text events YARG reads: the part's settings and drum mixes the model holds, and the rest kept as they're
        /// written.
        /// </summary>
        private void Texts()
        {
            foreach (var text in _track.Texts)
            {
                _taken[text.Index] = true;
                string written = TextOf(_events[text.Index], text.Type)!;
                if (Kind == ChartInstrumentKind.FiveFret && text.Text == "ENHANCED_OPENS")
                    _part.EnhancedOpens = true;
                else if (IsDrums && text.Text == "ENABLE_CHART_DYNAMICS")
                    _part.ChartDynamics = true;
                else if (IsDrums && DrumMix.TryParse(written, text.Tick, out var difficulty, out var mix))
                    _part[difficulty].Mixes.Add(mix);
                else
                    _part.TextEvents.Add(new TextEvent(text.Tick, text.Type, written));
            }
        }

        /// <summary>
        /// The note and Phase Shift events the reader paired with nothing, at a pitch or of a kind the model reads, which
        /// could pair with the model's notes once written back.
        /// </summary>
        private void Unpaired()
        {
            var consumed = _track.Consumed;
            for (int i = 1; i < _events.Count; i++)
            {
                if (_taken[i] || consumed.Contains(i))
                    continue;
                var e = _events[i];
                if (e.Kind is 0x80 or 0x90 && IsRead(_bytes[e.DataStart]))
                {
                    _taken[i] = true;
                    Issue(ChartLoadIssueKind.UnpairedNoteEvent, null, e.Tick, _bytes[e.DataStart]);
                }
                else if (!IsDrums && MidiTrackNotes.PhaseShiftOf(_bytes, e) is (int difficulty, int code, _) && IsReadPhaseShift(difficulty, code))
                {
                    _taken[i] = true;
                    Issue(ChartLoadIssueKind.UnpairedSysexEvent, difficulty <= 3 ? (ChartDifficulty)difficulty : null, e.Tick, null);
                }
            }
        }

        /// <summary>Everything else: text events as text, other events raw, at their ticks. The end of the track is left to the writer.</summary>
        private void Rest()
        {
            for (int i = 0; i < _events.Count; i++)
            {
                if (_taken[i])
                    continue;
                var e = _events[i];
                if (e.IsMeta)
                {
                    var (type, start, length) = MidiFile.MetaOf(_bytes, e);
                    if (type == MidiFile.EndOfTrack)
                        continue;
                    if (IsText(type))
                    {
                        _part.TextEvents.Add(new TextEvent(e.Tick, (byte)type, MidiFile.DecodeText(_bytes.AsSpan(start, length))));
                        continue;
                    }
                }

                _part.RawEvents.Add(RawOf(_bytes, e));
            }
        }

        /// <summary>
        /// Of <paramref name="candidates"/>, in the order they're let go, those the reader keeps: in tick order, the
        /// first of each key at a tick. The rest are left out with an issue, except those <paramref name="kept"/> says
        /// the model keeps as raw events.
        /// </summary>
        private List<Candidate> KeepFirst(List<Candidate> candidates, ChartDifficulty difficulty, Func<Candidate, int> keyOf, Func<Candidate, bool>? kept = null)
        {
            TickOrder.Sort(candidates, static c => c.Note.Start);
            var sorted = candidates.ToList();
            TickOrder.KeepFirstOfEach(candidates, static c => c.Note.Start, keyOf);

            // Those kept are in the order sorted, so the rest are found walking both.
            int next = 0;
            foreach (var candidate in sorted)
            {
                bool isKept = next < candidates.Count && candidates[next] == candidate;
                if (isKept)
                    next++;
                if (kept?.Invoke(candidate) == true)
                    continue;
                Take(candidate.Note);
                if (!isKept)
                    Issue(ChartLoadIssueKind.DuplicateNote, difficulty, candidate.Note.Start, candidate.Note.Pitch);
            }

            return candidates;
        }

        /// <summary>
        /// The Phase Shift open and tap phrases the reader reads, for a difficulty or for every one, all taken: they
        /// become open notes and tap chords.
        /// </summary>
        private List<Marker> PhaseShiftMarkers()
        {
            var markers = new List<Marker>();
            foreach (var sysex in _track.Sysex)
            {
                if (!IsReadPhaseShift(sysex.Difficulty, sysex.Code))
                    continue;
                _taken[sysex.OnIndex] = _taken[sysex.OffIndex] = true;
                markers.Add(new Marker(new TickSpan(sysex.Start, sysex.End), sysex.Code, sysex.Difficulty));
            }

            return markers;
        }

        /// <summary>The Phase Shift phrases of <paramref name="code"/> for <paramref name="difficulty"/>, as <see cref="MidiTrackNotes.SysexFor"/> gives them.</summary>
        private static List<Marker> For(List<Marker> sysex, ChartDifficulty difficulty, int code) =>
            [.. sysex.Where(m => m.Pitch == code && m.Difficulty == (int)difficulty), .. sysex.Where(m => m.Pitch == code && m.Difficulty == MidiLayout.SysexAllDifficulties)];

        /// <summary>The markers on <paramref name="pitch"/>, all taken: they become the flags of the chords they cover.</summary>
        private List<Marker> MarkersOn(int pitch) => [.. NotesOn(pitch).Select(n => new Marker(new TickSpan(n.Start, n.End), n.Pitch, null))];

        /// <summary>The notes on <paramref name="pitch"/>, of any channel, in the order they're let go, all taken unless not to <paramref name="take"/>.</summary>
        private List<MidiNote> NotesOn(int pitch, bool take = true)
        {
            var notes = new List<MidiNote>();
            foreach (var note in _track.Notes)
            {
                if (note.Pitch != pitch)
                    continue;
                if (take)
                    Take(note);
                notes.Add(note);
            }

            return notes;
        }

        /// <summary>Whether any of <paramref name="markers"/> covers <paramref name="tick"/>, marking those that do as used.</summary>
        private static bool Cover(List<Marker> markers, long tick)
        {
            bool covered = false;
            foreach (var marker in markers)
            {
                if (marker.Span.Covers(tick))
                {
                    marker.Used = true;
                    covered = true;
                }
            }

            return covered;
        }

        private void Unused(List<Marker> markers, ChartDifficulty? difficulty)
        {
            foreach (var marker in markers)
            {
                if (!marker.Used)
                    Issue(ChartLoadIssueKind.UnusedMarker, difficulty, marker.Span.Start, marker.Pitch);
            }
        }

        /// <summary>Whether the model reads note events on <paramref name="pitch"/>, so that one paired with nothing can't be kept.</summary>
        private bool IsRead(int pitch) =>
            MidiLayout.DifficultyOf(Kind, pitch) is not null
            || pitch is MidiLayout.SoloPitch or MidiLayout.StarPowerPitch
            || pitch is >= MidiLayout.DrumFillPitch and < MidiLayout.DrumFillPitch + 5
            || (IsDrums
                ? pitch is (>= MidiLayout.FirstTomPitch and <= MidiLayout.FirstTomPitch + 2) or MidiLayout.RollPitch or MidiLayout.TrillPitch
                : pitch == MidiLayout.TapPitch);

        /// <summary>Whether a fret track's Phase Shift phrase is one the reader reads: an open or a tap, for a difficulty or for every one.</summary>
        private static bool IsReadPhaseShift(int difficulty, int code) =>
            code is MidiLayout.SysexOpen or MidiLayout.SysexTap && difficulty is <= 3 or MidiLayout.SysexAllDifficulties;

        private void Take(MidiNote note) => _taken[note.OnIndex] = _taken[note.OffIndex] = true;

        private string? TextOf(MidiEvent e, int type) => MidiFile.TextOf(_bytes, e, type);

        private int? PitchOf(MidiEvent e) => e.Kind is 0x80 or 0x90 ? _bytes[e.DataStart] : null;

        private void Issue(ChartLoadIssueKind kind, ChartDifficulty? difficulty, long tick, int? pitch) =>
            _issues.Add(new ChartLoadIssue(kind, _part.Instrument, difficulty, tick, pitch));

        /// <summary>A drum note's accent or ghost by its note-on velocity, as YARG reads them once dynamics are on.</summary>
        private static NoteDynamics DynamicsOf(int velocity) => velocity switch
        {
            MidiLayout.AccentVelocity => NoteDynamics.Accent,
            MidiLayout.GhostVelocity => NoteDynamics.Ghost,
            _ => NoteDynamics.None,
        };

        /// <summary>
        /// The lowest difficulty a roll or trill reaches, by its velocity (charter-spec §7.2): 21 to 30 Easy, 31 to 40
        /// Medium, 41 to 50 Hard, and Expert alone otherwise.
        /// </summary>
        private static ChartDifficulty LowestDifficultyOf(int velocity) => velocity switch
        {
            >= 21 and <= 30 => ChartDifficulty.Easy,
            >= 31 and <= 40 => ChartDifficulty.Medium,
            >= 41 and <= 50 => ChartDifficulty.Hard,
            _ => ChartDifficulty.Expert,
        };
    }
}
