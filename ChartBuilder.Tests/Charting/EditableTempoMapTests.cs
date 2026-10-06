using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

[TestClass]
public sealed class EditableTempoMapTests
{
    private static readonly int[] Resolutions = [192, 480, 960];
    private static readonly int[] Denominators = [2, 4, 8, 16];

    // AC C4.3.
    [TestMethod]
    public void TickSecondsRoundTripsAgreeWithTheExistingTempoMapWithinHalfATick()
    {
        var random = new Random(4_3);
        var maps = new List<(EditableTempoMap Editable, TempoMap Reference)> { Fixed() };
        for (int m = 0; m < 50; m++)
            maps.Add(RandomMap(random));

        foreach (var (editable, reference) in maps)
        {
            var map = editable.ToTempoMap();
            Assert.IsTrue(map.SameAs(reference));
            for (int i = 0; i < 200; i++)
            {
                double tick = random.NextDouble() * 200_000;
                Assert.AreEqual(tick, reference.TickAt(map.SecondsAt(tick)), 0.5);
                Assert.AreEqual(tick, map.TickAt(reference.SecondsAt(tick)), 0.5);
                Assert.AreEqual(tick, map.TickAt(map.SecondsAt(tick)), 0.5);
            }

            Assert.AreSequenceEqual(reference.BeatLines(0, 50_000).ToList(), map.BeatLines(0, 50_000).ToList());
        }
    }

    [TestMethod]
    public void TheTempoMapIsKeptWhileNothingChanges()
    {
        var (map, _) = Fixed();

        var first = map.ToTempoMap();
        map.Anchors.Add(new TempoAnchor(1920, 2_000_000));

        Assert.AreSame(first, map.ToTempoMap());
    }

    [TestMethod]
    [DataRow("AddTempo")]
    [DataRow("RemoveTempo")]
    [DataRow("RemoveTempoAt")]
    [DataRow("ClearTempos")]
    [DataRow("AddTimeSignature")]
    [DataRow("RemoveTimeSignature")]
    [DataRow("RemoveTimeSignatureAt")]
    [DataRow("ClearTimeSignatures")]
    public void EachEditOfTheMarkersMakesANewTempoMap(string edit)
    {
        var (map, _) = Fixed();
        var before = map.ToTempoMap();

        switch (edit)
        {
            case "AddTempo": map.Tempos.Add(new TempoChange(960, 250_000)); break;
            case "RemoveTempo": Assert.IsTrue(map.Tempos.Remove(new TempoChange(1920, 1_000_000))); break;
            case "RemoveTempoAt": map.Tempos.RemoveAt(1); break;
            case "ClearTempos": map.Tempos.Clear(); break;
            case "AddTimeSignature": map.TimeSignatures.Add(new TimeSignatureMarker(960, 7, 8)); break;
            case "RemoveTimeSignature": Assert.IsTrue(map.TimeSignatures.Remove(new TimeSignatureMarker(3840, 3, 4))); break;
            case "RemoveTimeSignatureAt": map.TimeSignatures.RemoveAt(1); break;
            case "ClearTimeSignatures": map.TimeSignatures.Clear(); break;
            default: Assert.Fail(edit); break;
        }

        var after = map.ToTempoMap();
        var expected = new TempoMap(map.Resolution, map.Tempos, map.TimeSignatures.Select(s => s.ToTimeSignature()));
        Assert.AreNotSame(before, after);
        Assert.IsFalse(after.SameAs(before));
        Assert.IsTrue(after.SameAs(expected));
    }

    [TestMethod]
    public void TheMarkersAreKeptAsWrittenWithoutATickZeroTempo()
    {
        var map = new EditableTempoMap(480);
        map.Tempos.Add(new TempoChange(960, 400_000));
        map.Tempos.Add(new TempoChange(480, 600_000));
        map.Tempos.Add(new TempoChange(960, 300_000));

        Assert.AreSequenceEqual(
            [new TempoChange(480, 600_000), new TempoChange(960, 400_000), new TempoChange(960, 300_000)], map.Tempos.ToList());
        Assert.IsEmpty(map.TimeSignatures);

        // The tempo map tidies them as the games read them: 120 BPM from the start, and the last of two at one tick.
        Assert.AreSequenceEqual(
            [new TempoChange(0, 500_000), new TempoChange(480, 600_000), new TempoChange(960, 300_000)], map.ToTempoMap().Tempos.ToList());
    }

    [TestMethod]
    public void AnAnchorIsKeptOnePerTick()
    {
        var map = new EditableTempoMap(480);

        Assert.IsTrue(map.Anchors.Add(new TempoAnchor(1920, 2_000_000)));
        Assert.IsFalse(map.Anchors.Add(new TempoAnchor(1920, 3_000_000)));
        Assert.AreSequenceEqual([new TempoAnchor(1920, 2_000_000)], map.Anchors.ToList());
    }

    [TestMethod]
    public void TimeSignaturesKeepTheirMetronomeBytes()
    {
        var map = new EditableTempoMap(480);
        map.TimeSignatures.Add(new TimeSignatureMarker(0, 6, 8, 36, 8));

        Assert.AreEqual(new TimeSignatureMarker(0, 6, 8, 36, 8), map.TimeSignatures[0]);
        Assert.AreEqual(new TimeSignature(0, 6, 8), map.TimeSignatures[0].ToTimeSignature());
    }

    [TestMethod]
    public void MarkersMidiCantHoldAreRejected()
    {
        var map = new EditableTempoMap(480);

        Assert.ThrowsExactly<ArgumentException>(() => map.Tempos.Add(new TempoChange(0, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => map.Tempos.Add(new TempoChange(0, EditableTempoMap.MaxMicrosecondsPerQuarter + 1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => map.Tempos.Add(new TempoChange(-1, 500_000)));
        Assert.ThrowsExactly<ArgumentException>(() => map.TimeSignatures.Add(new TimeSignatureMarker(0, 4, 3)));
        Assert.ThrowsExactly<ArgumentException>(() => map.TimeSignatures.Add(new TimeSignatureMarker(0, 0, 4)));
        Assert.IsEmpty(map.Tempos);
        Assert.IsEmpty(map.TimeSignatures);
    }

    [TestMethod]
    [DataRow(120.0, 500_000L)]
    [DataRow(140.0, 428_571L)]
    [DataRow(133.333, 450_001L)]
    [DataRow(90.5, 662_983L)]
    [DataRow(200.001, 299_999L)]
    public void ABpmIsStoredAsWholeMicrosecondsToTheNearest(double bpm, long microseconds)
    {
        Assert.AreEqual(microseconds, EditableTempoMap.MicrosecondsPerQuarterOf(bpm));
    }

    [TestMethod]
    public void TheBpmShownComesFromTheMicrosecondsStored()
    {
        Assert.AreEqual(120, EditableTempoMap.BpmOf(500_000), 1e-12);
        Assert.AreEqual(140.00014, EditableTempoMap.BpmOf(428_571), 1e-6);

        var random = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            long microseconds = random.NextInt64(1, EditableTempoMap.MaxMicrosecondsPerQuarter + 1);
            Assert.AreEqual(microseconds, EditableTempoMap.MicrosecondsPerQuarterOf(EditableTempoMap.BpmOf(microseconds)));
        }
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(-120.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(1.0)]
    [DataRow(200_000_000.0)]
    public void ABpmMidiCantHoldIsRejected(double bpm)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EditableTempoMap.MicrosecondsPerQuarterOf(bpm));
    }

    // 120 BPM for four beats, then 60 BPM; 4/4, then 3/4 from the third measure.
    private static (EditableTempoMap Editable, TempoMap Reference) Fixed()
    {
        var map = new EditableTempoMap(480);
        map.Tempos.Add(new TempoChange(0, 500_000));
        map.Tempos.Add(new TempoChange(1920, 1_000_000));
        map.TimeSignatures.Add(new TimeSignatureMarker(0, 4, 4));
        map.TimeSignatures.Add(new TimeSignatureMarker(3840, 3, 4));
        return (map, new TempoMap(480, [new TempoChange(0, 500_000), new TempoChange(1920, 1_000_000)], [new TimeSignature(0, 4, 4), new TimeSignature(3840, 3, 4)]));
    }

    // Several tempos and time signatures at random ticks, sometimes none at tick 0 and sometimes two at one tick.
    private static (EditableTempoMap Editable, TempoMap Reference) RandomMap(Random random)
    {
        int resolution = Resolutions[random.Next(Resolutions.Length)];
        var map = new EditableTempoMap(resolution);
        var tempos = new List<TempoChange>();
        var signatures = new List<TimeSignature>();

        int tempoCount = random.Next(1, 9);
        for (int i = 0; i < tempoCount; i++)
        {
            long tick = i == 0 && random.Next(3) > 0 ? 0 : random.Next(0, 40) * (long)resolution / random.Next(1, 4);
            var tempo = new TempoChange(tick, EditableTempoMap.MicrosecondsPerQuarterOf(40 + (random.NextDouble() * 260)));
            map.Tempos.Add(tempo);
            tempos.Add(tempo);
        }

        int signatureCount = random.Next(0, 5);
        for (int i = 0; i < signatureCount; i++)
        {
            var signature = new TimeSignatureMarker(random.Next(0, 20) * 4L * resolution, random.Next(1, 13), Denominators[random.Next(Denominators.Length)]);
            map.TimeSignatures.Add(signature);
            signatures.Add(signature.ToTimeSignature());
        }

        // The reference takes them in the order they were added, which TempoMap tidies; the editable map sorts them first.
        return (map, new TempoMap(resolution, tempos.OrderBy(t => t.Tick), signatures.OrderBy(s => s.Tick)));
    }
}
