using System.Globalization;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class RetickTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    // Keeping times, on every fixture.

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void KeepingTimesLeavesEveryItemWithinHalfATickOfItsTime(string name)
    {
        // AC C4.2.
        var (chart, _) = Loaded(name);
        var before = chart.Clone();
        var (from, to) = Retempo(chart);

        var (report, _) = ChartRetick.Apply(chart, TickMaps.KeepTimes(from, to));

        Assert.IsEmpty(report.Collisions, string.Join("; ", report.Collisions.Select(c => c.Description)));
        var times = new Times(from, to);
        times.Kept("tempo track event", before.TempoTrackRaw, chart.TempoTrackRaw, static e => e.Tick, static e => e.Tick, static e => e.At(0));
        times.Kept("EVENTS text event", before.Events, chart.Events, static e => e.Tick, static e => e.Tick, static e => e with { Tick = 0 });
        times.Kept("EVENTS raw event", before.EventsRaw, chart.EventsRaw, static e => e.Tick, static e => e.Tick, static e => e.At(0));
        foreach (var part in before.Parts)
        {
            var moved = chart.PartOf(part.Instrument)!;
            foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
            {
                string where = $"{part.Instrument} {difficulty}";
                times.Kept($"{where} note", part[difficulty].Notes, moved[difficulty].Notes, static n => n.Tick, static n => n.Tick + n.Length, static n => n with { Tick = 0, Length = 0 });
                times.Kept($"{where} chord marking", part[difficulty].FlaggedChords, moved[difficulty].FlaggedChords, static c => c.Tick, static c => c.Tick, static c => c with { Tick = 0 });
                times.Kept($"{where} mix", part[difficulty].Mixes, moved[difficulty].Mixes, static m => m.Tick, static m => m.Tick, static m => m with { Tick = 0 });
            }

            string at = $"{part.Instrument}";
            times.Kept($"{at} phrase", part.Phrases, moved.Phrases, static p => p.Tick, static p => p.Tick + p.Length, static p => p with { Tick = 0, Length = 0 });
            times.Kept($"{at} tom marker", part.TomMarkers, moved.TomMarkers, static m => m.Tick, static m => m.Tick + m.Length, static m => m with { Tick = 0, Length = 0 });
            times.Kept($"{at} text event", part.TextEvents, moved.TextEvents, static e => e.Tick, static e => e.Tick, static e => e with { Tick = 0 });
            times.Kept($"{at} raw event", part.RawEvents, moved.RawEvents, static e => e.Tick, static e => e.Tick, static e => e.At(0));
        }

        // Each passthrough event keeps its time and its bytes, in its order.
        Assert.HasCount(before.Passthrough.Count, chart.Passthrough);
        int events = 0;
        for (int i = 0; i < before.Passthrough.Count; i++)
        {
            var original = before.Passthrough[i];
            var moved = chart.Passthrough[i];
            if (!original.IsTrack)
            {
                Assert.AreSame(original, moved);
                continue;
            }

            var (oldBytes, oldEvents) = EventsOf(original);
            var (newBytes, newEvents) = EventsOf(moved);
            Assert.HasCount(oldEvents.Count, newEvents, $"{original}");
            for (int j = 0; j < oldEvents.Count; j++)
            {
                times.Near($"{original} event {j}", oldEvents[j].Tick, newEvents[j].Tick);
                Assert.AreEqual(oldEvents[j].Status, newEvents[j].Status, $"{original} event {j}");
                Assert.AreSequenceEqual(
                    oldBytes.AsSpan(oldEvents[j].DataStart, oldEvents[j].DataLength).ToArray(),
                    newBytes.AsSpan(newEvents[j].DataStart, newEvents[j].DataLength).ToArray(),
                    $"{original} event {j}");
                if (oldEvents[j].Tick != newEvents[j].Tick)
                    events++;
            }

            Assert.AreEqual(ReferenceEquals(original, moved), moved.IsOriginal, $"{original}");
            Assert.AreEqual(original.Name, moved.Name);
        }

        Assert.AreEqual(events, report.PassthroughEvents);
        Assert.AreEqual(chart.Passthrough.Count(p => !p.IsOriginal), report.PassthroughTracks);
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void ARetickedChartWritesAndReadsBackAsItIs(string name)
    {
        var (chart, after) = Loaded(name);
        var (from, to) = Retempo(chart);
        ChartRetick.Apply(chart, TickMaps.KeepTimes(from, to));

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, $"{DescribeModel(result.ModelDifferences)} {GameViewTests.Describe(result.GameDifferences)} {string.Join("; ", result.ReloadIssues)}");
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void RestoringTheSnapshotPutsTheChartBackExactly(string name)
    {
        var (chart, _) = Loaded(name);
        var before = chart.Clone();
        var tempos = chart.TempoMap.Tempos.AsSpan().ToArray();
        var (from, to) = Retempo(chart);
        var (_, previous) = ChartRetick.Apply(chart, TickMaps.KeepTimes(from, to));

        previous.Restore();
        ChartTickSnapshot.Refill(chart.TempoMap.Tempos, tempos);

        var differences = ChartModelComparer.Differences(before, chart);
        Assert.IsEmpty(differences, DescribeModel(differences));
        for (int i = 0; i < before.Passthrough.Count; i++)
        {
            Assert.AreSame(before.Passthrough[i], chart.Passthrough[i]);
            Assert.IsTrue(chart.Passthrough[i].IsOriginal);
        }
    }

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void AnUnchangedTempoMapChangesNothingAndKeepsPassthroughBytes(string name)
    {
        var (chart, _) = Loaded(name);
        var before = chart.Clone();
        var map = chart.TempoMap.ToTempoMap();

        var (report, previous) = ChartRetick.Apply(chart, TickMaps.KeepTimes(map, chart.TempoMap.Clone().ToTempoMap()));

        Assert.AreEqual(new RetickReport(0, report.Collisions, 0, 0), report);
        Assert.IsEmpty(report.Collisions);
        Assert.IsTrue(previous.IsEmpty);
        Assert.IsEmpty(ChartModelComparer.Differences(before, chart));
        for (int i = 0; i < before.Passthrough.Count; i++)
        {
            Assert.AreSame(before.Passthrough[i], chart.Passthrough[i]);
            Assert.IsTrue(chart.Passthrough[i].IsOriginal);
        }
    }

    // Collisions.

    [TestMethod]
    public void ATempoChangeThatSqueezesTwoNotesOntoATickKeepsTheFirstAndReportsTheOther()
    {
        var chart = NewChart(ChartInstrument.Guitar);
        var expert = chart.PartOf(ChartInstrument.Guitar)![Expert];
        expert.Notes.Add(new EditableNote(480, 100, 0));
        expert.Notes.Add(new EditableNote(481, 200, 0));
        expert.Notes.Add(new EditableNote(481, 1, 1));
        expert.SetFlags(480, ChordFlags.ForceHopo);
        expert.SetFlags(481, ChordFlags.Tap);
        var from = chart.TempoMap.ToTempoMap();

        // A tenth of the tempo: ten old ticks to a new one.
        ChartTickSnapshot.Refill(chart.TempoMap.Tempos, [new TempoChange(0, 5_000_000)]);
        var (report, _) = ChartRetick.Apply(chart, TickMaps.KeepTimes(from, chart.TempoMap.ToTempoMap()));

        Assert.AreSequenceEqual([new EditableNote(48, 10, 0), new EditableNote(48, 0, 1)], expert.Notes.ToList());
        Assert.AreSequenceEqual([new FlaggedChord(48, ChordFlags.ForceHopo)], expert.FlaggedChords.ToList());
        Assert.HasCount(2, report.Collisions);
        var (note, marking) = (report.Collisions[0], report.Collisions[1]);
        Assert.AreEqual((ModelDifferenceKind.Note, ChartInstrument.Guitar, Expert, 481L, 48L), (note.Kind, note.Part, note.Difficulty, note.FromTick, note.ToTick));
        Assert.Contains("lane 0", note.Description);
        Assert.AreEqual((ModelDifferenceKind.ChordFlags, 481L, 48L), (marking.Kind, marking.FromTick, marking.ToTick));
        Assert.AreEqual(5, report.Moved); // 3 notes and 2 markings moved, 2 of them then left out.
    }

    // Passthrough tracks.

    [TestMethod]
    public void AChunkThatIsNotATrackIsNeverReticked()
    {
        var other = new PassthroughTrack([.. "XFIH"u8, 0, 0, 0, 2, 1, 2], null);
        var chart = NewChart(ChartInstrument.Guitar);
        chart.AddPassthrough(other);

        var (report, _) = ChartRetick.Apply(chart, static t => t * 2);

        Assert.AreSame(other, other.Retick(static t => t * 2));
        Assert.AreSame(other, chart.Passthrough[0]);
        Assert.IsTrue(other.IsOriginal);
        Assert.AreEqual(0, report.PassthroughTracks);
    }

    [TestMethod]
    public void ReticksATrackWithRunningStatusEventByEventWithEveryStatusWritten()
    {
        // A note-on, then another and its note-off by running status, a sysex, then the end of the track.
        byte[] chunk = Chunk(0, 0x90, 60, 100, 10, 62, 100, 5, 60, 0, 0, 0xF0, 2, 1, 0xF7, 20, 0xFF, 0x2F, 0);
        byte[] copy = [.. chunk];
        var track = new PassthroughTrack(chunk, "PART VOCALS");

        var moved = track.Retick(static t => t * 2, out int events);

        Assert.AreSequenceEqual(Chunk(0, 0x90, 60, 100, 20, 0x90, 62, 100, 10, 0x90, 60, 0, 0, 0xF0, 2, 1, 0xF7, 40, 0xFF, 0x2F, 0), moved.Chunk.ToArray());
        Assert.AreEqual(4, events); // All but the first, at tick 0.
        Assert.AreEqual("PART VOCALS", moved.Name);
        Assert.IsFalse(moved.IsOriginal);
        Assert.IsTrue(track.IsOriginal);
        Assert.AreSequenceEqual(copy, track.Chunk.ToArray());
    }

    [TestMethod]
    public void ATrackWhoseEventsKeepTheirTicksIsReturnedAsItIs()
    {
        var track = new PassthroughTrack(Chunk(0, 0x90, 60, 100, 10, 60, 0, 0, 0xFF, 0x2F, 0), "VENUE");

        Assert.AreSame(track, track.Retick(static t => t));
        Assert.AreSame(track, track.Retick(static t => t == 5 ? 6 : t));
    }

    [TestMethod]
    public void AnEventTheMapWouldPutBeforeAnEarlierOneStaysWithIt()
    {
        var track = new PassthroughTrack(Chunk(10, 0x90, 60, 100, 10, 0x80, 60, 0, 0, 0xFF, 0x2F, 0), null);

        var moved = track.Retick(static t => t == 20 ? 5 : t);

        Assert.AreSequenceEqual(Chunk(10, 0x90, 60, 100, 0, 0x80, 60, 0, 0, 0xFF, 0x2F, 0), moved.Chunk.ToArray());
    }

    [TestMethod]
    public void ARetickedTrackIsEqualToTheTrackWithItsBytes()
    {
        var track = new PassthroughTrack(Chunk(0, 0x90, 60, 100, 10, 0x80, 60, 0, 0, 0xFF, 0x2F, 0), "VENUE");

        var back = track.Retick(static t => t * 3).Retick(static t => t / 3);

        Assert.IsFalse(back.IsOriginal);
        Assert.AreEqual(track, back);
        Assert.AreEqual(track.GetHashCode(), back.GetHashCode());
    }

    // The tick map.

    [TestMethod]
    public void TheKeepTimesMapNeverDecreases()
    {
        var (chart, _) = Loaded(nameof(MidiFixtures.TempoMap));
        var (from, to) = Retempo(chart);
        var map = TickMaps.KeepTimes(from, to);
        var squeeze = TickMaps.KeepTimes(to, from);

        var random = new Random(42);
        var ticks = Enumerable.Range(0, 5_000).Select(_ => (long)random.Next(0, 20_000)).Concat([0L, 1, 1919, 1920, 1921, 3839, 3840, 3841]).Order().ToList();
        for (int i = 1; i < ticks.Count; i++)
        {
            Assert.IsLessThanOrEqualTo(map(ticks[i]), map(ticks[i - 1]), string.Create(CultureInfo.InvariantCulture, $"{ticks[i - 1]} and {ticks[i]}"));
            Assert.IsLessThanOrEqualTo(squeeze(ticks[i]), squeeze(ticks[i - 1]), string.Create(CultureInfo.InvariantCulture, $"{ticks[i - 1]} and {ticks[i]}, squeezed"));
        }
    }

    [TestMethod]
    public void TheKeepTimesMapOfATempoMapToItselfIsTheIdentity()
    {
        var (chart, _) = Loaded(nameof(MidiFixtures.TempoMap));
        var map = chart.TempoMap.ToTempoMap();
        var same = TickMaps.KeepTimes(map, map);
        var copy = TickMaps.KeepTimes(map, chart.TempoMap.Clone().ToTempoMap());

        var random = new Random(7);
        foreach (long tick in Enumerable.Range(0, 1_000).Select(_ => (long)random.Next(0, 1_000_000)))
        {
            Assert.AreEqual(tick, same(tick));
            Assert.AreEqual(tick, copy(tick));
        }
    }

    // Helpers.

    /// <summary>A fixture loaded, and the settings it's read with once saved.</summary>
    private static (EditableChart Chart, ChartReadOptions After) Loaded(string name)
    {
        var fixture = MidiFixtures.Named(name);
        var chart = MidiChartLoader.Load(fixture.Midi, fixture.Options).Chart;
        return (chart, MidiChartWriter.SettingsAfterSave(chart, fixture.Options));
    }

    /// <summary>
    /// Changes <paramref name="chart"/>'s tempo map: every tempo marker a fifth faster, at its tick, and a marker at tick
    /// 1000 faster still (or, where there's one, that one made faster). Its anchors stay on their markers.
    /// </summary>
    /// <returns>The tempo map before and after.</returns>
    private static (TempoMap From, TempoMap To) Retempo(EditableChart chart)
    {
        var from = chart.TempoMap.ToTempoMap();
        var tempos = chart.TempoMap.Tempos.Select(t => t with { MicrosecondsPerQuarter = t.MicrosecondsPerQuarter * 4 / 5 }).ToList();
        if (!tempos.Any(t => t.Tick == 1000))
        {
            long at = from.Tempos.Last(t => t.Tick <= 1000).MicrosecondsPerQuarter;
            tempos.Add(new TempoChange(1000, at * 7 / 10));
        }

        ChartTickSnapshot.Refill(chart.TempoMap.Tempos, [.. tempos.OrderBy(t => t.Tick)]);
        return (from, chart.TempoMap.ToTempoMap());
    }

    /// <summary>A track chunk of <paramref name="data"/>.</summary>
    private static byte[] Chunk(params byte[] data) => [.. "MTrk"u8, 0, 0, 0, (byte)data.Length, .. data];

    /// <summary>A passthrough track's bytes and events.</summary>
    private static (byte[] Bytes, List<MidiEvent> Events) EventsOf(PassthroughTrack track)
    {
        byte[] bytes = track.Chunk.ToArray();
        return (bytes, MidiFile.ReadEvents(bytes, new MidiChunk("MTrk", 0, PassthroughTrack.HeaderLength, bytes.Length - PassthroughTrack.HeaderLength)));
    }

    /// <summary>Checks that items moved from one tempo map to another keep their times.</summary>
    private sealed class Times(TempoMap from, TempoMap to)
    {
        /// <summary>
        /// Checks that each item of <paramref name="before"/> is in <paramref name="after"/> with its start and end each
        /// within half a tick of their times. Items alike but for their ticks are matched in tick order.
        /// </summary>
        public void Kept<T>(string what, IEnumerable<T> before, IEnumerable<T> after, Func<T, long> tickOf, Func<T, long> endOf, Func<T, T> alike)
            where T : notnull
        {
            var expected = before.GroupBy(alike).ToDictionary(g => g.Key, g => g.OrderBy(tickOf).ThenBy(endOf).ToList());
            var actual = after.GroupBy(alike).ToDictionary(g => g.Key, g => g.OrderBy(tickOf).ThenBy(endOf).ToList());
            Assert.HasCount(expected.Count, actual, what);
            foreach (var (key, items) in expected)
            {
                Assert.IsTrue(actual.TryGetValue(key, out var moved), $"No {what} like {key} is left.");
                Assert.HasCount(items.Count, moved, $"{what} {key}");
                for (int i = 0; i < items.Count; i++)
                {
                    Near(what, tickOf(items[i]), tickOf(moved[i]));
                    Near($"{what} end", endOf(items[i]), endOf(moved[i]));
                }
            }
        }

        /// <summary>Checks that <paramref name="newTick"/> of the new map is within half a tick of the time <paramref name="oldTick"/> had.</summary>
        public void Near(string what, long oldTick, long newTick)
        {
            double exact = to.TickAt(from.SecondsAt(oldTick));
            Assert.IsLessThanOrEqualTo(0.5 + 1e-6, Math.Abs(newTick - exact),
                string.Create(CultureInfo.InvariantCulture, $"The {what} at tick {oldTick} moved to {newTick}; {exact:0.###} keeps its time."));
        }
    }
}
