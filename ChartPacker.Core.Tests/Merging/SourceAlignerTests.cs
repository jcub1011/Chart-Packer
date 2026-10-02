using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Tests.Charts;

namespace ChartPacker.Core.Tests.Merging;

[TestClass]
public sealed class SourceAlignerTests
{
    // At 120 BPM and 480 ticks a beat, a beat is half a second.
    private const int Beat = 480;

    private static readonly long[] Gaps = [120, 240, 360, 480, 720];

    [TestMethod]
    public void FirstNoteIsHowFarApartTheFirstNotesAre()
    {
        var a = Guitar([960, 1440]);
        var b = Guitar([480, 960]);

        var estimate = SourceAligner.FirstNote(a, b, "guitar", ChartDifficulty.Expert);

        Assert.AreEqual(AlignmentMethod.FirstNote, estimate.Method);
        Assert.AreEqual(500, estimate.OffsetMs);
        Assert.IsFalse(estimate.IsReliable);
    }

    [TestMethod]
    public void FirstNoteCountsTheDelays()
    {
        var a = Guitar([0], delayMs: 200);
        var b = Guitar([0], delayMs: -50);

        Assert.AreEqual(250, SourceAligner.FirstNote(a, b, "guitar", ChartDifficulty.Expert).OffsetMs);
    }

    [TestMethod]
    public void NotePatternFindsAKnownOffset()
    {
        long[] rhythm = Rhythm(60, seed: 1);
        var a = Guitar(rhythm);
        var b = Guitar(rhythm, delayMs: -730);

        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);

        Assert.AreEqual(730, estimate.OffsetMs);
        Assert.IsTrue(estimate.IsReliable);
        Assert.IsFalse(estimate.IsNarrowed);
        Assert.AreEqual(1.0, estimate.Match, 0.01);
        Assert.AreEqual(0, estimate.DriftMs);
        Assert.IsFalse(estimate.HasDrift);
    }

    [TestMethod]
    public void NotePatternLinesUpAChartWithAnIntroTheOtherDoesntHave()
    {
        // The first chart has eight beats of intro before the part both have; the second starts with the part.
        long[] rhythm = Rhythm(60, seed: 2);
        var a = Guitar([.. Enumerable.Range(0, 8).Select(i => (long)i * Beat), .. rhythm.Select(t => t + (8 * Beat))]);
        var b = Guitar(rhythm);

        Assert.AreEqual(0, SourceAligner.FirstNote(a, b, "guitar", ChartDifficulty.Expert).OffsetMs);
        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);
        Assert.AreEqual(4000, estimate.OffsetMs);
        Assert.IsTrue(estimate.IsReliable);
    }

    [TestMethod]
    public void NotePatternIsntThrownByNotesOnlyOneChartHas()
    {
        long[] rhythm = Rhythm(80, seed: 3);
        var random = new Random(4);
        long[] fewer = [.. rhythm.Where(_ => random.Next(5) != 0)];
        long[] extra = [.. Enumerable.Range(0, 10).Select(_ => (long)random.Next(0, (int)rhythm[^1]))];
        var a = Guitar(rhythm);
        var b = Guitar([.. fewer.Concat(extra).Distinct().Order()], delayMs: 1234);

        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);

        Assert.AreEqual(-1234, estimate.OffsetMs);
        Assert.IsTrue(estimate.IsReliable);
    }

    [TestMethod]
    public void SteadyNotesAreLookedForNearTheFirstNotes()
    {
        // A note every beat lines up as well a beat out as not: only the first notes tell them apart.
        long[] steady = [.. Enumerable.Range(0, 64).Select(i => (long)i * Beat)];
        var a = Guitar(steady, delayMs: 40);
        var b = Guitar(steady);

        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);

        Assert.IsTrue(estimate.IsNarrowed);
        Assert.IsTrue(estimate.IsReliable);
        Assert.AreEqual(40, estimate.OffsetMs);
    }

    [TestMethod]
    public void NotesSteadierThanHalfABeatStayAmbiguous()
    {
        long[] eighths = [.. Enumerable.Range(0, 128).Select(i => (long)i * Beat / 2)];
        var a = Guitar(eighths);
        var b = Guitar(eighths);

        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);

        Assert.IsTrue(estimate.IsAmbiguous);
        Assert.IsFalse(estimate.IsReliable);
    }

    [TestMethod]
    public void KicksAndSnaresTellASteadyHiHatApart()
    {
        // Eighth-note hi-hats on their own line up on every eighth; the kicks and snares under them don't.
        var random = new Random(5);
        var builder = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART DRUMS");
        for (int i = 0; i < 128; i++)
        {
            long tick = (long)i * Beat / 2;
            builder.Note(tick, 98);
            if (random.Next(3) == 0)
                builder.Note(tick, 96);
            else if (random.Next(3) == 0)
                builder.Note(tick, 97);
        }

        byte[] midi = builder.Build();
        var a = AlignmentChart.Of(new MidiChart(midi, TimeSpan.Zero, ChartReadOptions.Default));
        var b = AlignmentChart.Of(new MidiChart(midi, TimeSpan.FromMilliseconds(-1000), ChartReadOptions.Default));

        var estimate = SourceAligner.NotePattern(a, b, "drums", ChartDifficulty.Expert);

        Assert.AreEqual(1000, estimate.OffsetMs);
        Assert.IsTrue(estimate.IsReliable);
    }

    [TestMethod]
    public void ASongPlayedFasterDrifts()
    {
        // The second chart's notes come 0.05% sooner each, as a song sped up a little would: about 30 ms over a minute.
        long[] rhythm = Rhythm(160, seed: 6);
        var a = Guitar(rhythm);
        var b = Guitar([.. rhythm.Select(t => (long)Math.Round(t * 0.9995))]);

        var estimate = SourceAligner.NotePattern(a, b, "guitar", ChartDifficulty.Expert);

        Assert.IsNotNull(estimate.DriftMs);
        Assert.IsTrue(estimate.HasDrift, $"Drift was {estimate.DriftMs} ms.");
        Assert.IsTrue(estimate.DriftMs > 0);
    }

    [TestMethod]
    public void VocalsLineUpByTheirNotes()
    {
        long[] rhythm = Rhythm(40, seed: 7);
        var a = Vocals(rhythm, delayMs: 300);
        var b = Vocals(rhythm);

        CollectionAssert.AreEqual(new[] { "vocals" }, a.PartIds.ToArray());
        Assert.AreEqual(300, SourceAligner.NotePattern(a, b, "vocals", null).OffsetMs);
    }

    [TestMethod]
    public void LinksAreMadeForEachSharedPartAtTheHardestDifficultyBothHave()
    {
        byte[] Midi(params int[] pitches)
        {
            var builder = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR");
            foreach (int pitch in pitches)
                builder.Note(0, pitch).Note(Beat, pitch);
            return builder.Track("PART BASS").Note(0, 96).Build();
        }

        // Expert green is 96, Hard green 84; the second chart has no Expert guitar, the third no guitar.
        var a = Of(Midi(96, 84));
        var b = Of(Midi(84));
        var c = Of(new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART DRUMS").Note(0, 96).Build());

        var links = SourceAligner.Links([a, b, c, null]);

        Assert.HasCount(4, links);
        var guitar = links.Single(l => l.From == 0 && l.To == 1 && l.PartId == "guitar");
        Assert.AreEqual(ChartDifficulty.Hard, guitar.Difficulty);
        Assert.IsTrue(links.Any(l => l.From == 1 && l.To == 0 && l.PartId == "bass"));
        Assert.IsFalse(links.Any(l => l.From == 2 || l.To == 2));
    }

    [TestMethod]
    public void SongsWithoutAPartInCommonAreLinedUpThroughOneThatSharesWithBoth()
    {
        long[] rhythm = Rhythm(60, seed: 8);
        // A and B share the guitar, B and C the drums; B is 250 ms early against A, C 100 ms late against B.
        var a = Of(Midi(guitar: rhythm), delayMs: 250);
        var b = Of(Midi(guitar: rhythm, drums: rhythm));
        var c = Of(Midi(drums: rhythm), delayMs: 100);

        var links = SourceAligner.Links([a, b, c]);
        var choices = SourceAligner.Solve(0, links);

        Assert.AreEqual(0, choices[1].Link.From);
        Assert.AreEqual(1, choices[2].Link.From);
        Assert.AreEqual(AlignmentMethod.NotePattern, choices[2].Method);
        CollectionAssert.AreEqual(new[] { 0, 250, 150 }, SourceAligner.OffsetsOf(3, 0, choices));

        // A song nudged moves those lined up through it with it.
        CollectionAssert.AreEqual(new[] { 0, 260, 160 }, SourceAligner.OffsetsOf(3, 0, choices, new Dictionary<int, int> { [1] = 10 }));
    }

    [TestMethod]
    public void TheShortestRouteIsTaken()
    {
        long[] rhythm = Rhythm(60, seed: 9);
        var a = Of(Midi(guitar: rhythm, bass: rhythm));
        var b = Of(Midi(guitar: rhythm, drums: rhythm));
        var c = Of(Midi(drums: rhythm, bass: rhythm), delayMs: -80);

        var choices = SourceAligner.Solve(0, SourceAligner.Links([a, b, c]));

        Assert.AreEqual(0, choices[2].Link.From);
        Assert.AreEqual("bass", choices[2].Link.PartId);
        Assert.AreEqual(80, choices[2].OffsetMs);
    }

    [TestMethod]
    public void ASongNoLinkReachesMovesOnlyByItsNudge()
    {
        long[] rhythm = Rhythm(20, seed: 10);
        var a = Of(Midi(guitar: rhythm));
        var b = Of(Midi(drums: rhythm));

        var choices = SourceAligner.Solve(0, SourceAligner.Links([a, b, null]));

        Assert.IsEmpty(choices);
        CollectionAssert.AreEqual(new[] { 0, 0, -30 }, SourceAligner.OffsetsOf(3, 0, choices, new Dictionary<int, int> { [0] = 99, [2] = -30 }));
    }

    [TestMethod]
    public void SongsLinedUpWithEachOtherInACircleMoveOnlyByTheirNudges()
    {
        long[] rhythm = Rhythm(20, seed: 11);
        var links = SourceAligner.Links([Of(Midi(guitar: rhythm)), Of(Midi(drums: rhythm)), Of(Midi(drums: rhythm), delayMs: 40)]);
        var choices = new Dictionary<int, AlignmentChoice>
        {
            [1] = new(links.Single(l => l.From == 2 && l.To == 1), AlignmentMethod.FirstNote),
            [2] = new(links.Single(l => l.From == 1 && l.To == 2), AlignmentMethod.FirstNote),
        };

        Assert.IsFalse(SourceAligner.IsLinked(1, 0, choices, new HashSet<int>()));
        CollectionAssert.AreEqual(new[] { 0, 5, 0 }, SourceAligner.OffsetsOf(3, 0, choices, new Dictionary<int, int> { [1] = 5 }));
    }

    [TestMethod]
    public void EstimatesTurnAroundWithTheirLinks()
    {
        long[] rhythm = Rhythm(60, seed: 12);
        var link = SourceAligner.Links([Guitar(rhythm), Guitar(rhythm, delayMs: 90)]).Single(l => l.From == 0);
        var back = link.Reversed();

        Assert.AreEqual(-90, link.Pattern.OffsetMs);
        Assert.AreEqual((1, 0), (back.From, back.To));
        Assert.AreEqual(90, back.Pattern.OffsetMs);
        Assert.AreEqual(90, back.FirstNote.OffsetMs);
        Assert.AreEqual(AlignmentMethod.NotePattern, back.PreferredMethod);
    }

    /// <summary>Note ticks with gaps of an eighth to a beat and a half, the same for a seed.</summary>
    private static long[] Rhythm(int count, int seed, long start = 0)
    {
        var random = new Random(seed);
        var ticks = new long[count];
        long tick = start;
        for (int i = 0; i < count; i++)
        {
            ticks[i] = tick;
            tick += Gaps[random.Next(Gaps.Length)];
        }

        return ticks;
    }

    private static byte[] Midi(long[]? guitar = null, long[]? drums = null, long[]? bass = null)
    {
        var builder = new MidiBuilder().Track("tempo").Tempo(0, 120);
        foreach (var (name, ticks) in new[] { ("PART GUITAR", guitar), ("PART DRUMS", drums), ("PART BASS", bass) })
        {
            if (ticks is null)
                continue;
            builder.Track(name);
            foreach (long tick in ticks)
                builder.Note(tick, 96);
        }

        return builder.Build();
    }

    private static AlignmentChart Guitar(long[] ticks, int delayMs = 0) => Of(Midi(guitar: ticks), delayMs);

    private static AlignmentChart Vocals(long[] ticks, int delayMs = 0)
    {
        var builder = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART VOCALS");
        foreach (long tick in ticks)
            builder.Note(tick, 60, 60);
        return Of(builder.Build(), delayMs);
    }

    private static AlignmentChart Of(byte[] midi, int delayMs = 0) =>
        AlignmentChart.Of(new MidiChart(midi, TimeSpan.FromMilliseconds(delayMs), ChartReadOptions.Default));
}
