using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class ChartReadBackTests
{
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    private static readonly ChartTrackKey GuitarExpert = new(ChartInstrument.Guitar, Expert);
    private static readonly ChartTrackKey DrumsExpert = new(ChartInstrument.Drums, Expert);

    /// <summary>A change to <see cref="Sample"/>, and the one difference it should make.</summary>
    public sealed record Change(
        string Name,
        Func<ChartDocument> Actual,
        ReadBackDifferenceKind Kind,
        ChartInstrument? Instrument,
        ChartDifficulty? Difficulty,
        long[] Ticks,
        NoteFlags Flags = NoteFlags.None);

    private static readonly Change[] Changes =
    [
        new("TempoChanged", () => Sample(tempo: Tempo(480, 300_000)), ReadBackDifferenceKind.TempoMap, null, null, [1920]),
        new("TimeSignatureAdded", () => Sample(tempo: Tempo(480, 250_000, new TimeSignature(3840, 3, 4))), ReadBackDifferenceKind.TempoMap, null, null, [3840]),
        new("ResolutionChanged", () => Sample(tempo: Tempo(960, 250_000)), ReadBackDifferenceKind.TempoMap, null, null, [0]),
        new("DrumsLayoutChanged", () => Sample(pro: false), ReadBackDifferenceKind.DrumsLayout, ChartInstrument.Drums, null, [0]),
        new("FiveLaneDrums", () => Sample(fiveLane: true), ReadBackDifferenceKind.DrumsLayout, ChartInstrument.Drums, null, [0]),
        new("TrackMissing", () => Sample(change: t => t.Remove(GuitarExpert)), ReadBackDifferenceKind.MissingTrack, ChartInstrument.Guitar, Expert, [0, 480, 960]),
        new("TrackExtra", () => Sample(change: t => t[new(ChartInstrument.Guitar, Hard)] = new([new(240, 0, 0, NoteFlags.None)], [])),
            ReadBackDifferenceKind.ExtraTrack, ChartInstrument.Guitar, Hard, [240]),
        new("NoteMissing", () => Sample(change: t => Notes(t, GuitarExpert, n => n.Where(x => x.Tick != 960))),
            ReadBackDifferenceKind.MissingNotes, ChartInstrument.Guitar, Expert, [960]),
        new("NoteExtra", () => Sample(change: t => Notes(t, GuitarExpert, n => [.. n, new(1440, 0, 4, NoteFlags.None)])),
            ReadBackDifferenceKind.ExtraNotes, ChartInstrument.Guitar, Expert, [1440]),
        new("SustainChanged", () => Sample(change: t => Notes(t, GuitarExpert, n => n.Select(x => x.Tick == 480 ? x with { Length = 239 } : x))),
            ReadBackDifferenceKind.SustainLengths, ChartInstrument.Guitar, Expert, [480]),
        new("HopoChanged", () => Sample(change: t => Notes(t, GuitarExpert, n => n.Select(x => x.Tick == 480 ? x with { Flags = NoteFlags.None } : x))),
            ReadBackDifferenceKind.NoteFlags, ChartInstrument.Guitar, Expert, [480], NoteFlags.Hopo),
        new("AccentLost", () => Sample(change: t => Notes(t, DrumsExpert, n => n.Select(x => x with { Flags = x.Flags & ~NoteFlags.Accent }))),
            ReadBackDifferenceKind.NoteFlags, ChartInstrument.Drums, Expert, [0], NoteFlags.Accent),
        new("GhostBecameCymbal", () => Sample(change: t => Notes(t, DrumsExpert, n => n.Select(x => x.Tick == 480 ? x with { Flags = NoteFlags.Cymbal } : x))),
            ReadBackDifferenceKind.NoteFlags, ChartInstrument.Drums, Expert, [480], NoteFlags.Ghost | NoteFlags.Cymbal),
        new("StarPowerChanged", () => Sample(change: t => t[GuitarExpert] = new(t[GuitarExpert].Notes, [new(960, 240)])),
            ReadBackDifferenceKind.StarPower, ChartInstrument.Guitar, Expert, [960]),
    ];

    public static IEnumerable<object[]> ChangeNames => Changes.Select(c => new object[] { c.Name });

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public void IdenticalDocumentsHaveNoDifferences(string name)
    {
        var fixture = MidiFixtures.Named(name);

        var differences = ChartReadBack.Compare(
            MidiChartReader.Read(fixture.Midi, fixture.Options),
            MidiChartReader.Read(fixture.Midi, fixture.Options),
            Enum.GetValues<ChartInstrument>());

        Assert.IsEmpty(differences);
        Assert.IsEmpty(ChartReadBack.Compare(Sample(), Sample(), Enum.GetValues<ChartInstrument>()));
    }

    [TestMethod]
    [DynamicData(nameof(ChangeNames))]
    public void EachKindOfDifferenceIsReportedWithWhereAndWhenItIs(string name)
    {
        var change = Changes.Single(c => c.Name == name);
        var expected = Sample();

        var differences = ChartReadBack.Compare(expected, change.Actual(), Enum.GetValues<ChartInstrument>());

        var difference = Assert.ContainsSingle(differences);
        Assert.AreEqual(change.Kind, difference.Kind);
        Assert.AreEqual(change.Instrument, difference.Instrument);
        Assert.AreEqual(change.Difficulty, difference.Difficulty);
        Assert.AreSequenceEqual(change.Ticks, difference.Ticks.ToList());
        Assert.AreSequenceEqual([.. change.Ticks.Select(t => TimeSpan.FromSeconds(expected.Tempo.SecondsAt(t)))], difference.Times.ToList());
        Assert.AreEqual(change.Ticks.Length, difference.Count);
        Assert.AreEqual(change.Flags, difference.Flags);
    }

    [TestMethod]
    public void TimesAreTheExpectedTempoMaps()
    {
        // 120 BPM to tick 1920, then 240.
        var difference = ChartReadBack.Compare(Sample(), Sample(tempo: Tempo(480, 250_000, new TimeSignature(2880, 3, 4))), [ChartInstrument.Guitar]).Single();

        Assert.AreSequenceEqual([TimeSpan.FromSeconds(2.5)], difference.Times.ToList());
    }

    [TestMethod]
    public void ANoteInAnotherLaneIsBothMissingAndExtra()
    {
        var actual = Sample(change: t => Notes(t, GuitarExpert, n => n.Select(x => x.Tick == 960 ? x with { Lane = 3 } : x)));

        var differences = ChartReadBack.Compare(Sample(), actual, [ChartInstrument.Guitar]);

        Assert.AreSequenceEqual(
            [ReadBackDifferenceKind.MissingNotes, ReadBackDifferenceKind.ExtraNotes],
            differences.Select(d => d.Kind).ToList());
        Assert.IsTrue(differences.All(d => d.Ticks.SequenceEqual([960L])));
    }

    [TestMethod]
    public void InstrumentsOutsideTheListAreIgnored()
    {
        var actual = Sample(fiveLane: true, change: t =>
        {
            t[new ChartTrackKey(ChartInstrument.Bass, Expert)] = new([new(0, 0, 0, NoteFlags.None)], []);
            t.Remove(DrumsExpert);
            Notes(t, new ChartTrackKey(ChartInstrument.Keys, Expert), _ => [new(0, 0, 1, NoteFlags.None)]);
        });

        Assert.IsEmpty(ChartReadBack.Compare(Sample(), actual, [ChartInstrument.Guitar]));
        Assert.HasCount(4, ChartReadBack.Compare(Sample(), actual, [ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums, ChartInstrument.Keys]));
    }

    [TestMethod]
    public void TheTicksListedAreCappedAndCounted()
    {
        int count = ChartReadBack.MaxTicks + 10;
        var expected = Sample(change: t => Notes(t, GuitarExpert, _ => Enumerable.Range(0, count).Select(i => new ChartNote(i * 120L, 240, 0, NoteFlags.None))));
        var actual = Sample(change: t => Notes(t, GuitarExpert, _ => Enumerable.Range(0, count).Select(i => new ChartNote(i * 120L, 0, 0, NoteFlags.None))));

        var difference = ChartReadBack.Compare(expected, actual, [ChartInstrument.Guitar]).Single();

        Assert.AreEqual(ReadBackDifferenceKind.SustainLengths, difference.Kind);
        Assert.AreEqual(count, difference.Count);
        Assert.AreSequenceEqual([.. Enumerable.Range(0, ChartReadBack.MaxTicks).Select(i => i * 120L)], difference.Ticks.ToList());
        Assert.HasCount(ChartReadBack.MaxTicks, difference.Times);
    }

    /// <summary>
    /// Pro drums and five-fret guitar on Expert, at 120 BPM until tick 1920 and 240 from there: a plain note, a HOPO
    /// sustain and a note in star power; a kick and an accented yellow cymbal, and a ghost red.
    /// </summary>
    private static ChartDocument Sample(TempoMap? tempo = null, bool pro = true, bool fiveLane = false, Action<Dictionary<ChartTrackKey, NoteTrack>>? change = null)
    {
        var tracks = new Dictionary<ChartTrackKey, NoteTrack>
        {
            [GuitarExpert] = new(
                [new(0, 0, 0, NoteFlags.None), new(480, 240, 1, NoteFlags.Hopo), new(960, 0, 2, NoteFlags.StarPower)],
                [new(960, 480)]),
            [DrumsExpert] = new(
                [new(0, 0, ChartNote.OpenLane, NoteFlags.None), new(0, 0, 1, NoteFlags.Cymbal | NoteFlags.Accent), new(480, 0, 0, NoteFlags.Ghost)],
                []),
        };
        change?.Invoke(tracks);
        return new ChartDocument(tempo ?? Tempo(480, 250_000), tracks, null, fiveLane) { IsProDrums = pro };
    }

    /// <summary>120 BPM in 4/4, then from tick 1920 a beat <paramref name="microsecondsPerQuarter"/> long.</summary>
    private static TempoMap Tempo(int resolution, long microsecondsPerQuarter, params TimeSignature[] signatures) =>
        new(resolution, [new TempoChange(0, 500_000), new TempoChange(1920, microsecondsPerQuarter)], [new TimeSignature(0, 4, 4), .. signatures]);

    /// <summary>Puts the notes <paramref name="change"/> makes of the track's (none if it has none) in place of them.</summary>
    private static void Notes(Dictionary<ChartTrackKey, NoteTrack> tracks, ChartTrackKey key, Func<IEnumerable<ChartNote>, IEnumerable<ChartNote>> change)
    {
        var track = tracks.GetValueOrDefault(key);
        tracks[key] = new NoteTrack([.. change(track?.Notes ?? [])], track?.StarPower ?? []);
    }
}
