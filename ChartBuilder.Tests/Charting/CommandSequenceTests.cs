using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>Random sequences of every kind of command, undone and redone through the undo stack.</summary>
[TestClass]
public sealed class CommandSequenceTests
{
    private const int Steps = 200;

    public static IEnumerable<object[]> FixturesAndSeeds =>
        MidiFixtures.All.SelectMany(f => new[] { new object[] { f.Name, 1 }, [f.Name, 2] });

    [TestMethod]
    [DynamicData(nameof(FixturesAndSeeds))]
    public void UndoingEveryCommandGivesTheStartAndRedoingThemGivesTheEnd(string name, int seed)
    {
        // AC C15.1.
        var chart = Loaded(name);
        var start = chart.Clone();
        var stack = new ChartUndoStack(chart);

        var kinds = Run(stack, new Random((MidiFixtures.All.ToList().FindIndex(f => f.Name == name) * 100) + seed));
        var end = chart.Clone();

        Assert.IsGreaterThanOrEqualTo(15, kinds.Count, $"Only {string.Join(", ", kinds)} were run.");
        while (stack.CanUndo)
            stack.Undo();
        AssertSame(start, chart, "after undoing every command");
        Assert.IsFalse(stack.IsDirty);
        while (stack.CanRedo)
            stack.Redo();
        AssertSame(end, chart, "after redoing every command");
    }

    [TestMethod]
    [DataRow(nameof(MidiFixtures.LeadGuitar))]
    [DataRow(nameof(MidiFixtures.ProDrums))]
    [DataRow(nameof(MidiFixtures.SixFret))]
    public void AChartEditedByRandomCommandsSavesAndReadsBackAsItIs(string name)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = Loaded(name);
        var stack = new ChartUndoStack(chart);
        Run(stack, new Random(3));

        var result = MidiChartWriter.WriteChecked(chart, MidiChartWriter.SettingsAfterSave(chart, fixture.Options));

        Assert.IsTrue(result.IsValid, $"{DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
    }

    [TestMethod]
    public void TenThousandEditsKeepTheUndoStackWellUnderItsBudget()
    {
        // charter-spec §11: under 64 MB for a session of 10,000 edits. Generous, as the GC's count isn't exact.
        var chart = NewChart(ChartInstrument.Guitar);
        var stack = new ChartUndoStack(chart);
        long before = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 2_500; i++)
        {
            var note = new EditableNote(i * 120L, 1, i % 5);
            stack.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [note]));
            stack.Execute(new SetSustain(ChartInstrument.Guitar, ChartDifficulty.Expert, [note], 60));
            stack.Execute(new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [note.Tick], ChordFlags.ForceStrum, on: true));
            stack.Execute(new MoveNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [note with { Length = 60 }], 60, 0));
        }

        long used = GC.GetTotalMemory(forceFullCollection: true) - before;
        Assert.AreEqual(10_000, stack.UndoCount);
        Assert.IsLessThan(64L * 1024 * 1024, used);
        GC.KeepAlive(stack);
    }

    /// <summary>Executes <see cref="Steps"/> random commands that apply, skipping those the chart refuses.</summary>
    /// <returns>The kinds of command executed.</returns>
    private static HashSet<string> Run(ChartUndoStack stack, Random random)
    {
        var generator = new RandomCommands(random);
        var kinds = new HashSet<string>();
        int executed = 0;
        for (int attempt = 0; executed < Steps && attempt < Steps * 20; attempt++)
        {
            if (generator.Next(stack.Chart) is not { } command)
                continue;
            try
            {
                stack.Execute(command);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                // Refused, as the generator doesn't look ahead (a move onto a note, say). A refusal changes nothing,
                // which undoing back to the start checks.
                continue;
            }

            kinds.Add(command.GetType().Name);
            executed++;
        }

        Assert.AreEqual(Steps, executed);
        return kinds;
    }

    private static EditableChart Loaded(string name)
    {
        var fixture = MidiFixtures.Named(name);
        return MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;
    }

    /// <summary>
    /// Makes random commands of every kind from what a chart holds, keeping to what saves and reads back as it is (no
    /// fifth pad on drums that aren't 5-lane, and drum fills and big rock endings as <c>[coda]</c> makes them).
    /// </summary>
    internal sealed class RandomCommands(Random random)
    {
        private const int Snap = 60;
        private const int Snaps = 256;
        private int _texts;

        public IChartCommand? Next(EditableChart chart) => random.Next(110) switch
        {
            < 16 => AddNotes(chart),
            < 23 => RemoveNotes(chart),
            < 33 => MoveNotes(chart),
            < 39 => SetSustain(chart),
            < 42 => SetDynamics(chart),
            < 48 => SetChordFlags(chart),
            < 52 => AddPhrase(chart),
            < 54 => RemovePhrase(chart),
            < 57 => ChangePhrase(chart),
            < 61 => AddText(chart),
            < 63 => RemoveText(chart),
            < 66 => new AddTempo(new TempoChange(Tick(), random.Next(300_000, 1_000_000))),
            < 68 => Maybe(chart.TempoMap.Tempos.Where(t => t.Tick > 0)) is { } tempo ? new RemoveTempo(tempo) : null,
            < 71 => Maybe(chart.TempoMap.Tempos) is { } tempo ? new SetTempo(tempo, random.Next(300_000, 1_000_000)) : null,
            < 73 => new AddTimeSignature(new TimeSignatureMarker(random.Next(Snaps / 8) * 480L, random.Next(2, 8), random.Next(2) == 0 ? 4 : 8)),
            < 74 => Maybe(chart.TempoMap.TimeSignatures.Where(s => s.Tick > 0)) is { } signature ? new RemoveTimeSignature(signature) : null,
            < 76 => Maybe(chart.TempoMap.TimeSignatures) is { } signature ? new SetTimeSignature(signature, random.Next(2, 8), random.Next(2) == 0 ? 4 : 8) : null,
            < 79 => SetAnchor(chart),
            < 80 => Maybe(chart.TempoMap.Anchors) is { } anchor ? new ClearAnchor(anchor.Tick) : null,
            < 84 => ChangeTempoMap(chart),
            < 86 => AddPart(chart),
            < 87 => chart.Parts.Count > 1 ? new RemovePart(Any(chart.Parts).Instrument) : null,
            < 90 => SetToms(chart),
            < 92 => SetDoubleKick(chart),
            < 95 => DiscoFlip(chart),
            < 97 => Coda(chart),
            < 98 => chart.PartOf(ChartInstrument.Drums) is not null && chart.DrumsVariant is not null ? DrumVariantChange.For(chart, (DrumsVariant)random.Next(3)) : null,
            _ => Gesture(chart),
        };

        private IChartCommand? AddNotes(EditableChart chart)
        {
            if (Part(chart.Parts) is not { } part)
                return null;
            var track = part[(ChartDifficulty)random.Next(4)];
            var notes = new List<EditableNote>();
            for (int i = random.Next(1, 4); i > 0; i--)
            {
                var note = NoteFor(chart, track);
                if (track.Notes.IndexOfPlace(note) < 0 && !notes.Any(n => n.Tick == note.Tick && n.Lane == note.Lane && n.DoubleKick == note.DoubleKick))
                    notes.Add(note);
            }

            return notes.Count > 0 ? new AddNotes(part.Instrument, track.Difficulty, notes) : null;
        }

        private IChartCommand? RemoveNotes(EditableChart chart) =>
            TrackWithNotes(chart) is var (part, track) ? new RemoveNotes(part, track.Difficulty, SomeNotes(track)) : null;

        private IChartCommand? MoveNotes(EditableChart chart)
        {
            if (TrackWithNotes(chart) is not var (part, track))
                return null;
            var note = Any(track.Notes);
            var notes = random.Next(2) == 0 ? track.Notes.At(note.Tick).ToArray() : [note];
            long tickDelta = (random.Next(2) == 0 ? -1 : 1) * Snap * random.Next(1, 9);
            int laneDelta = random.Next(-1, 2);
            if (track.Kind == ChartInstrumentKind.Drums && ((chart.DrumsVariant != DrumsVariant.FiveLane && notes.Any(n => n.Lane + laneDelta == 5)) || notes.Any(n => n.DoubleKick)))
                laneDelta = 0;
            return new MoveNotes(part, track.Difficulty, notes, tickDelta, laneDelta);
        }

        private IChartCommand? SetSustain(EditableChart chart) =>
            TrackWithNotes(chart) is var (part, track) ? new SetSustain(part, track.Difficulty, SomeNotes(track), random.Next(1, 8) * Snap) : null;

        // A kick is always a normal hit, so only pads are given.
        private IChartCommand? SetDynamics(EditableChart chart) =>
            TrackWithNotes(chart, ChartInstrumentKind.Drums) is var (part, track) && SomeNotes(track).Where(n => n.Lane > 0).ToList() is { Count: > 0 } pads
                ? new SetDynamics(part, track.Difficulty, pads, (NoteDynamics)random.Next(3))
                : null;

        private IChartCommand? SetChordFlags(EditableChart chart)
        {
            if (TrackWithNotes(chart, notKind: ChartInstrumentKind.Drums) is not var (part, track))
                return null;
            long[] ticks = [.. SomeNotes(track).Select(n => n.Tick).Distinct()];
            return new SetChordFlags(part, track.Difficulty, ticks, (ChordFlags)(1 << random.Next(3)), random.Next(3) > 0);
        }

        private IChartCommand? AddPhrase(EditableChart chart)
        {
            if (Part(chart.Parts) is not { } part)
                return null;
            PhraseKind[] kinds = part.Kind == ChartInstrumentKind.Drums
                ? [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.DrumFill, PhraseKind.Roll, PhraseKind.Trill]
                : [PhraseKind.StarPower, PhraseKind.Solo, PhraseKind.BigRockEnding];
            var kind = kinds[random.Next(kinds.Length)];
            long tick = Tick();
            if (kind == PhraseKind.DrumFill)
                kind = DrumFills.KindAt(chart, tick);
            ChartDifficulty? lowest = Phrase.HasLowestDifficulty(kind) ? (ChartDifficulty)random.Next(4) : null;
            return new AddPhrase(part.Instrument, new Phrase(kind, tick, random.Next(1, 32) * Snap, lowest));
        }

        private IChartCommand? RemovePhrase(EditableChart chart) =>
            Part(chart.Parts.Where(p => p.Phrases.Count > 0)) is { } part ? new RemovePhrase(part.Instrument, Any(part.Phrases)) : null;

        private IChartCommand? ChangePhrase(EditableChart chart)
        {
            if (Part(chart.Parts.Where(p => p.Phrases.Count > 0)) is not { } part)
                return null;
            var phrase = Any(part.Phrases);
            var changed = phrase with
            {
                Tick = Math.Max(0, phrase.Tick + (random.Next(-4, 5) * Snap)),
                Length = random.Next(1, 32) * Snap,
                LowestDifficulty = phrase.LowestDifficulty is null ? null : (ChartDifficulty)random.Next(4),
            };

            // Drums' fills and big rock endings are told apart by [coda], so one moved past it becomes the other.
            if (part.Kind == ChartInstrumentKind.Drums && phrase.Kind is PhraseKind.DrumFill or PhraseKind.BigRockEnding)
                changed = changed with { Kind = DrumFills.KindAt(chart, changed.Tick) };
            return new ChangePhrase(part.Instrument, phrase, changed);
        }

        // Pro drum notes made toms or cymbals.
        private IChartCommand? SetToms(EditableChart chart)
        {
            if (chart.DrumsVariant != DrumsVariant.Pro || chart.PartOf(ChartInstrument.Drums) is not { } drums)
                return null;
            var pads = drums.Tracks.SelectMany(t => t.Notes).Where(n => n.Lane is >= TomMarker.FirstPad and <= TomMarker.LastPad).ToList();
            if (pads.Count == 0)
                return null;
            var places = new HashSet<(int, long)>();
            for (int i = random.Next(1, 4); i > 0; i--)
            {
                var note = Any(pads);
                places.Add((note.Lane, note.Tick));
            }

            return new SetToms(places, random.Next(2) == 0);
        }

        private IChartCommand? SetDoubleKick(EditableChart chart)
        {
            if (chart.PartOf(ChartInstrument.Drums)?[ChartDifficulty.Expert] is not { } track)
                return null;
            bool on = random.Next(2) == 0;
            var kicks = track.Notes.Where(n => n.Lane == 0 && n.DoubleKick != on).ToList();
            return kicks.Count > 0 ? new SetDoubleKick([Any(kicks)], on) : null;
        }

        // A disco flip section added, removed, moved or resized.
        private IChartCommand? DiscoFlip(EditableChart chart)
        {
            if (chart.PartOf(ChartInstrument.Drums) is not { } drums)
                return null;
            var difficulty = (ChartDifficulty)random.Next(4);
            var ranges = DiscoFlips.RangesOf(drums[difficulty]);
            long tick = Tick();
            long end = tick + (random.Next(1, 16) * Snap);
            return random.Next(3) switch
            {
                0 => new AddDiscoFlip(difficulty, tick, end),
                1 => ranges.Count > 0 ? new RemoveDiscoFlip(difficulty, Any(ranges)) : null,
                _ => ranges.Count > 0 ? new ChangeDiscoFlip(difficulty, Any(ranges), tick, end) : null,
            };
        }

        // A [coda] added or removed, which makes the drums' fills and big rock endings what it then decides.
        private IChartCommand? Coda(EditableChart chart)
        {
            if (random.Next(2) == 0)
                return new AddTextEvent(null, TextEvent.Of(Tick(), "[coda]"));
            return Maybe(chart.Events.Where(DrumFills.IsCoda)) is { } coda ? new RemoveTextEvent(null, coda) : null;
        }

        private IChartCommand? AddText(EditableChart chart)
        {
            _texts++;
            if (random.Next(2) == 0 || chart.Parts.Count == 0)
                return new AddTextEvent(null, TextEvent.Of(Tick(), $"[section generated_{_texts}]"));
            return new AddTextEvent(Any(chart.Parts).Instrument, TextEvent.Of(Tick(), $"generated {_texts}"));
        }

        private IChartCommand? RemoveText(EditableChart chart)
        {
            if (random.Next(2) == 0)
                return Maybe(chart.Events.Where(e => e.Text.StartsWith("[section", StringComparison.Ordinal))) is { } section ? new RemoveTextEvent(null, section) : null;
            return Part(chart.Parts.Where(p => p.TextEvents.Count > 0)) is { } part ? new RemoveTextEvent(part.Instrument, Any(part.TextEvents)) : null;
        }

        private IChartCommand? SetAnchor(EditableChart chart)
        {
            if (Maybe(chart.TempoMap.Tempos) is not { } tempo)
                return null;
            return new SetAnchor(tempo.Tick, (long)(chart.TempoMap.ToTempoMap().SecondsAt(tempo.Tick) * 1_000_000) + random.Next(-5_000, 5_000));
        }

        private IChartCommand? ChangeTempoMap(EditableChart chart)
        {
            var (by, over) = random.Next(2) == 0 ? (4, 5) : (5, 4);
            var tempos = chart.TempoMap.Tempos.Select(t => t with { MicrosecondsPerQuarter = Math.Clamp(t.MicrosecondsPerQuarter * by / over, 1, EditableTempoMap.MaxMicrosecondsPerQuarter) });
            return new ChangeTempoMap(tempos, chart.TempoMap.TimeSignatures, chart.TempoMap.Anchors, keepTimes: random.Next(2) == 0);
        }

        private IChartCommand? AddPart(EditableChart chart)
        {
            if (Maybe(NewChartSettings.Instruments.Where(i => chart.PartOf(i) is null)) is not { } instrument)
                return null;
            DrumsVariant? variant = instrument == ChartInstrument.Drums && chart.DrumsVariant is null ? (DrumsVariant)random.Next(3) : null;
            return new AddPart(instrument, variant);
        }

        // A gesture: notes added as a chord, and the chord marked.
        private IChartCommand? Gesture(EditableChart chart)
        {
            if (Part(chart.Parts.Where(p => p.Kind != ChartInstrumentKind.Drums)) is not { } part)
                return null;
            var track = part[(ChartDifficulty)random.Next(4)];
            long tick = Tick();
            int lane = random.Next(0, 3);
            EditableNote[] chord = [new(tick, 1, lane), new(tick, 1, lane + 2)];
            return new CompositeCommand(
            [
                new AddNotes(part.Instrument, track.Difficulty, chord),
                new SetChordFlags(part.Instrument, track.Difficulty, [tick], (ChordFlags)(1 << random.Next(3)), on: true),
            ]);
        }

        private EditableNote NoteFor(EditableChart chart, EditableTrack track)
        {
            long tick = Tick();
            if (track.Kind != ChartInstrumentKind.Drums)
                return new EditableNote(tick, random.Next(3) == 0 ? random.Next(1, 8) * Snap : 1, random.Next(EditableNote.FirstLane(track.Kind), EditableNote.LastLane(track.Kind) + 1));

            int last = chart.DrumsVariant == DrumsVariant.FiveLane ? 5 : 4;
            int lane = random.Next(0, last + 1);
            bool doubleKick = lane == 0 && track.Difficulty == ChartDifficulty.Expert && random.Next(4) == 0;
            return new EditableNote(tick, 1, lane, lane > 0 ? (NoteDynamics)random.Next(3) : NoteDynamics.None, doubleKick);
        }

        private (ChartInstrument Part, EditableTrack Track)? TrackWithNotes(EditableChart chart, ChartInstrumentKind? kind = null, ChartInstrumentKind? notKind = null)
        {
            var tracks = chart.Parts
                .Where(p => (kind is null || p.Kind == kind) && (notKind is null || p.Kind != notKind))
                .SelectMany(p => p.Tracks.Where(t => t.Notes.Count > 0).Select(t => (p.Instrument, t)))
                .ToList();
            return tracks.Count > 0 ? tracks[random.Next(tracks.Count)] : null;
        }

        private List<EditableNote> SomeNotes(EditableTrack track)
        {
            var notes = new List<EditableNote>();
            for (int i = random.Next(1, 4); i > 0; i--)
            {
                var note = Any(track.Notes);
                if (!notes.Contains(note))
                    notes.Add(note);
            }

            return notes;
        }

        private long Tick() => random.Next(Snaps) * (long)Snap;

        /// <summary>One of <paramref name="items"/>, which has at least one.</summary>
        private T Any<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];

        /// <summary>One of <paramref name="items"/>, or null when there's none.</summary>
        private T? Maybe<T>(IEnumerable<T> items)
            where T : struct
        {
            T[] list = [.. items];
            return list.Length > 0 ? list[random.Next(list.Length)] : null;
        }

        /// <summary>One of <paramref name="parts"/>, or null when there's none.</summary>
        private EditablePart? Part(IEnumerable<EditablePart> parts)
        {
            EditablePart[] list = [.. parts];
            return list.Length > 0 ? list[random.Next(list.Length)] : null;
        }
    }
}
