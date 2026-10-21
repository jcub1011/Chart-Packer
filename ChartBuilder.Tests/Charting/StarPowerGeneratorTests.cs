using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// Generate star power (charter-spec C8 item 1): where <see cref="StarPowerGenerator"/> proposes phrases, how many, and
/// the meter it plans them by, on made-up guitar parts at 480 ticks per beat, 120 BPM in 4/4: a measure is 1,920 ticks
/// and two seconds.
/// </summary>
[TestClass]
public sealed class StarPowerGeneratorTests
{
    private const long Measure = 1920;
    private const long Beat = 480;
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private static readonly StarPowerParameters P = StarPowerParameters.Default;

    [TestMethod]
    public void TwoPhrasesGiveHalfAMeterThatLastsFourMeasures()
    {
        var played = StarPowerMeter.Simulate([1, 3], [5], P);

        Assert.HasCount(1, played);
        Assert.IsTrue(played[0].Fired);
        Assert.AreEqual(0.5, played[0].Meter, 1e-9);
        Assert.AreEqual(9, played[0].EndMeasure, 1e-9);
    }

    [TestMethod]
    public void OnePhraseIsntEnoughToActivate()
    {
        var played = StarPowerMeter.Simulate([1], [5], P);

        Assert.IsFalse(played[0].Fired);
        Assert.AreEqual(0.25, played[0].Meter, 1e-9);
    }

    [TestMethod]
    public void APhraseCompletedWhileStarPowerIsOnStretchesItByTwoMeasures()
    {
        var state = StarPowerMeter.StateAt([1, 3, 6], [5], 8, P);

        Assert.AreEqual(11, state.ActiveUntil!.Value, 1e-9);
        Assert.IsFalse(StarPowerMeter.StateAt([1, 3, 6], [5], 11, P).ActiveUntil.HasValue);
    }

    [TestMethod]
    public void TheMeterHoldsAtMostAFullBarOfEightMeasures()
    {
        var played = StarPowerMeter.Simulate([1, 2, 3, 4, 5, 6], [10], P);

        Assert.AreEqual(1, played[0].Meter, 1e-9);
        Assert.AreEqual(18, played[0].EndMeasure, 1e-9);
    }

    [TestMethod]
    public void ActivatingWhileStarPowerIsOnDoesNothing()
    {
        var played = StarPowerMeter.Simulate([1, 3, 6, 7], [8, 10], P);

        Assert.IsTrue(played[0].Fired);
        Assert.IsFalse(played[1].Fired);
        Assert.IsFalse(StarPowerMeter.AllFire([1, 3, 6, 7], [8, 10], P));
    }

    [TestMethod]
    public void MeasuresFollowTheTimeSignaturesSoASixEightMeasureCountsAsOne()
    {
        var chart = Chart(Steady(0, 8));
        new AddTimeSignature(new TimeSignatureMarker(2 * Measure, 6, 8)).Apply(chart);
        var timeline = new StarPowerTimeline(chart.TempoMap.ToTempoMap(), Chords(chart), P);

        // 6/8 is six eighths, 1,440 ticks a measure, from measure 2 on.
        Assert.AreEqual(2, timeline.MeasureAt(2 * Measure), 1e-9);
        Assert.AreEqual(3, timeline.MeasureAt((2 * Measure) + 1440), 1e-9);
        Assert.AreEqual(2.5, timeline.MeasureAt((2 * Measure) + 720), 1e-9);
        Assert.AreEqual((2 * Measure) + 1440, timeline.TickAt(3));
    }

    [TestMethod]
    public void ALongRestIsLeftOutOfThePartsPlaying()
    {
        // Measures 0 to 9, then nothing for 30 measures (60 s), then measures 40 to 49.
        var chart = Chart([.. Steady(0, 10), .. Steady(40, 10)]);
        var timeline = new StarPowerTimeline(chart.TempoMap.ToTempoMap(), Chords(chart), P);

        double before = timeline.ActiveSecondsAt(10 * Measure);
        double after = timeline.ActiveSecondsAt(40 * Measure);

        // The rest runs from a tick past measure 9's last quarter note to measure 40's first: measure 10's line is in it.
        Assert.AreEqual(after, before, 1e-9);
        Assert.AreEqual(49 - (40 - (9.75 + (1.0 / Measure))), timeline.ActiveMeasureAt(49 * Measure), 1e-9);
    }

    [TestMethod]
    public void APartsHardPassagesAreFoundWhereTheyStart()
    {
        var chart = Chart(VerseChorus(3));

        var generated = Propose(chart);

        var starts = generated.Spikes.Select(s => (double)s.StartTick / Measure).Order().ToList();
        Assert.HasCount(3, starts);
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(8 + (16 * i), starts[i], 1, $"Spike {i} starts at measure {starts[i]}.");
    }

    [TestMethod]
    public void EachHardPassageIsChargedToHalfAMeterJustBeforeIt()
    {
        var chart = Chart(VerseChorus(3));

        var generated = Propose(chart, new StarPowerOptions(SpacingSeconds: 12));

        var timeline = new StarPowerTimeline(chart.TempoMap.ToTempoMap(), Chords(chart), P);
        var charged = generated.Proposals.Where(p => p.Role == StarPowerRole.SpikeLeadIn).GroupBy(p => p.SpikeTick!.Value).ToList();
        Assert.HasCount(3, charged);

        var completions = generated.Proposals.Select(p => timeline.MeasureAt(LastChordIn(chart, p.Phrase))).ToList();
        var activations = charged.Select(g => timeline.MeasureAt(g.Key)).ToList();
        Assert.IsTrue(StarPowerMeter.AllFire(completions, activations, P));
        foreach (var group in charged)
        {
            double lead = timeline.ActiveMeasureAt(group.Key) - timeline.ActiveMeasureAt(LastChordIn(chart, group.Last().Phrase));
            Assert.IsTrue(lead >= P.MinLeadMeasures && lead <= P.MaxLeadMeasures, $"Lead {lead} before {group.Key}.");
        }
    }

    [TestMethod]
    public void APartAsHardThroughoutIsGivenPhrasesSpreadEvenly()
    {
        // 64 measures, 128 s, at one difficulty: six phrases, about 22 s apart.
        var chart = Chart(Steady(0, 64));

        var generated = Propose(chart);

        Assert.IsEmpty(generated.Spikes);
        Assert.HasCount(6, generated.Proposals);
        Assert.IsTrue(generated.Proposals.All(p => p.Role == StarPowerRole.Filler));
        var starts = generated.Proposals.Select(p => (double)p.Phrase.Tick / Measure).ToList();
        var gaps = starts.Zip(starts.Skip(1), static (a, b) => b - a).ToList();
        Assert.IsTrue(gaps.Max() - gaps.Min() <= 3, $"Starts at measures {string.Join(", ", starts)}.");
    }

    [TestMethod]
    public void TheCountFollowsThePartsPlayingNotTheSongsLength()
    {
        var steady = Propose(Chart(Steady(0, 64)));
        var withTacet = Propose(Chart([.. Steady(0, 32), .. Steady(62, 32)]));

        Assert.AreEqual(steady.Proposals.Count, withTacet.Proposals.Count);
        Assert.IsTrue(withTacet.Proposals.All(p => p.Phrase.CoverEnd <= 32 * Measure || p.Phrase.Tick >= 62 * Measure));
    }

    [TestMethod]
    public void AWholePartIsGivenAFullMetersWorthWhereTheyFit()
    {
        // 24 measures, 48 s: two at 22 s apart, but at least four.
        var generated = Propose(Chart(Steady(0, 24)));

        Assert.HasCount(4, generated.Proposals);
    }

    [TestMethod]
    public void APhraseRunsFromAMeasureLineToTheNextAfterItsLastChord()
    {
        // As the official charts mostly do: its last chord, a quarter note, is the last before the line it ends on.
        var chart = Chart(Steady(0, 64));

        foreach (var proposal in Propose(chart).Proposals)
        {
            Assert.AreEqual(0, proposal.Phrase.Tick % Measure, $"{proposal.Phrase}");
            Assert.AreEqual(0, proposal.Phrase.CoverEnd % Measure, $"{proposal.Phrase}");
            Assert.AreEqual(PhraseKind.StarPower, proposal.Phrase.Kind);
        }
    }

    [TestMethod]
    public void APhraseEndingFarFromAMeasureLineEndsOnTheBeatAfterItsLastNote()
    {
        // Eighths on each measure's first two beats, then a rest: the line is two and a half beats past the last note's
        // release, so the phrase ends on the third beat.
        var notes = new List<EditableNote>();
        for (long m = 0; m < 64; m++)
        {
            for (int e = 0; e < 4; e++)
                notes.Add(new EditableNote((m * Measure) + (e * Beat / 2), 1, e % 3));
        }

        var generated = Propose(Chart(notes));

        Assert.IsNotEmpty(generated.Proposals);
        foreach (var proposal in generated.Proposals)
            Assert.AreEqual(2 * Beat, proposal.Phrase.CoverEnd % Measure, $"{proposal.Phrase}");
    }

    [TestMethod]
    public void APhraseNeverRunsOnOverTheNextChord()
    {
        // Each measure's last note is held past the next measure line, where the next chord is: the phrase ends there.
        var notes = new List<EditableNote>();
        for (long m = 0; m < 64; m++)
        {
            for (int q = 0; q < 3; q++)
                notes.Add(new EditableNote((m * Measure) + (q * Beat), 1, q % 3));
            notes.Add(new EditableNote((m * Measure) + (3 * Beat), 600, 3));
        }

        var chart = Chart(notes);
        foreach (var proposal in Propose(chart).Proposals)
        {
            Assert.AreEqual(0, proposal.Phrase.CoverEnd % Measure, $"{proposal.Phrase}");
            Assert.IsTrue(chart.PartOf(Guitar)![Expert].Notes.At(proposal.Phrase.CoverEnd).Length > 0, "The next chord starts where it ends.");
        }
    }

    [TestMethod]
    public void NoPhraseCrossesASectionOrASolosEdgeOrOverlapsABigRockEnding()
    {
        var chart = Chart(Steady(0, 64));
        for (long m = 3; m < 64; m += 6)
            new AddTextEvent(null, TextEvent.Of(m * Measure, $"[section S{m}]")).Apply(chart);
        new AddPhrase(Guitar, new Phrase(PhraseKind.Solo, (10 * Measure) + Beat, 20 * Measure)).Apply(chart);
        new AddPhrase(Guitar, new Phrase(PhraseKind.BigRockEnding, 50 * Measure, 4 * Measure)).Apply(chart);

        var generated = Propose(chart);

        Assert.IsNotEmpty(generated.Proposals);
        var sections = SectionEvents.All(chart).Select(e => e.Tick).ToList();
        foreach (var phrase in generated.Proposals.Select(p => p.Phrase))
        {
            Assert.IsFalse(sections.Any(s => s > phrase.Tick && s < phrase.CoverEnd), $"{phrase} crosses a section.");
            Assert.IsFalse(phrase.Overlaps(new Phrase(PhraseKind.BigRockEnding, 50 * Measure, 4 * Measure)));
            var solo = new Phrase(PhraseKind.Solo, (10 * Measure) + Beat, 20 * Measure);
            Assert.IsTrue(!phrase.Overlaps(solo) || (phrase.Tick >= solo.Tick && phrase.CoverEnd <= solo.CoverEnd), $"{phrase} crosses the solo's edge.");
        }
    }

    [TestMethod]
    public void EveryPhraseCoversANoteOnEachDifficultyWithNotesSoNoneIsEmptyThere()
    {
        // Hard has a note only on measures' third beats.
        var chart = Chart(VerseChorus(3));
        for (long m = 0; m < 48; m++)
            chart.PartOf(Guitar)![ChartDifficulty.Hard].Notes.Add(new EditableNote((m * Measure) + (2 * Beat), 1, 0));

        var generated = Propose(chart, new StarPowerOptions(SpacingSeconds: 12));

        Assert.IsNotEmpty(generated.Proposals);
        StarPowerGenerator.CommandOf(Guitar, generated.Proposals)!.Apply(chart);
        var result = new ChartLinter().LintAll(new ChartLintInput(chart.Clone(), []));
        Assert.IsEmpty(result.FindingsOf(ChrRules.Chr060));
        Assert.IsEmpty(result.FindingsOf(ChrRules.Chr062));
        Assert.IsEmpty(result.FindingsOf(ChrRules.Chr065));
    }

    [TestMethod]
    public void StarPowerAlreadyThereStaysAndProposalsKeepAMeasureClearOfIt()
    {
        var chart = Chart(Steady(0, 64));
        var existing = new Phrase(PhraseKind.StarPower, 20 * Measure, Measure);
        new AddPhrase(Guitar, existing).Apply(chart);

        var generated = Propose(chart);

        Assert.IsNotEmpty(generated.Proposals);
        foreach (var phrase in generated.Proposals.Select(p => p.Phrase))
            Assert.IsTrue(phrase.CoverEnd + Measure <= existing.Tick || phrase.Tick >= existing.CoverEnd + Measure, $"{phrase} is too close.");
    }

    [TestMethod]
    public void NoPhraseCompletesSoLateItsStarPowerCantBeUsed()
    {
        var generated = Propose(Chart(Steady(0, 64)));

        // The last chord is at measure 63's fourth beat: a phrase completes two measures before it at the latest.
        Assert.IsTrue(generated.Proposals.All(p => p.Phrase.CoverEnd <= (62 * Measure) + (3 * Beat) + 1));
    }

    [TestMethod]
    public void OverASpanEveryPhraseLiesWithinIt()
    {
        var chart = Chart(Steady(0, 64));

        var generated = Propose(chart, span: (16 * Measure, (40 * Measure) - 1));

        Assert.IsNotEmpty(generated.Proposals);
        Assert.IsTrue(generated.Proposals.All(p => p.Phrase.Tick >= 16 * Measure && p.Phrase.CoverEnd <= 40 * Measure));
    }

    [TestMethod]
    public void PhrasesArePlacedAgainstTheHighestDifficultyWithNotes()
    {
        var chart = Chart([]);
        foreach (var note in Steady(0, 64))
            chart.PartOf(Guitar)![ChartDifficulty.Hard].Notes.Add(note);

        var generated = Propose(chart);

        Assert.AreEqual(ChartDifficulty.Hard, generated.Source);
        Assert.HasCount(6, generated.Proposals);
    }

    [TestMethod]
    public void KeysAreGivenStarPowerAsFiveFretIs()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Keys }, DrumsVariant.Pro));
        foreach (var note in Steady(0, 64))
            chart.PartOf(ChartInstrument.Keys)![Expert].Notes.Add(note);

        var generated = StarPowerGenerator.Propose(chart, ChartInstrument.Keys, StarPowerOptions.Default);

        Assert.AreEqual(NoStarPowerReason.None, generated.Reason);
        Assert.HasCount(6, generated.Proposals);
    }

    [TestMethod]
    public void NothingIsProposedForDrumsGhlAPartWithoutNotesOrOneTooSparse()
    {
        Assert.AreEqual(NoStarPowerReason.NotFiveFret, StarPowerGenerator.Propose(Editing.EditorSessions.Drums(), ChartInstrument.Drums, StarPowerOptions.Default).Reason);
        Assert.AreEqual(NoStarPowerReason.NotFiveFret, StarPowerGenerator.Propose(Editing.EditorSessions.Ghl(), ChartInstrument.GhlGuitar, StarPowerOptions.Default).Reason);
        Assert.AreEqual(NoStarPowerReason.NoNotes, Propose(Chart([])).Reason);
        Assert.AreEqual(NoStarPowerReason.NoSpotFits, Propose(Chart([new EditableNote(0, 1, 0), new EditableNote(Beat, 1, 1), new EditableNote(2 * Beat, 1, 2)])).Reason);
    }

    [TestMethod]
    public void ProposingChangesNothingAndGivesTheSameEachTime()
    {
        var chart = Chart(VerseChorus(3));
        var before = chart.Clone();

        var first = Propose(chart);
        var second = Propose(chart);

        Assert.IsEmpty(ChartModelComparer.Differences(before, chart));
        Assert.AreSequenceEqual(first.Proposals.ToList(), second.Proposals.ToList());
    }

    [TestMethod]
    public void TheCommandAddsEveryProposalAsOneUndoableStep()
    {
        var chart = Chart(Steady(0, 64));
        var stack = new ChartUndoStack(chart);
        var generated = Propose(chart);

        stack.Execute(StarPowerGenerator.CommandOf(Guitar, generated.Proposals)!);

        Assert.AreEqual(generated.Proposals.Count, chart.PartOf(Guitar)!.Phrases.Count(p => p.Kind == PhraseKind.StarPower));
        stack.Undo();
        Assert.IsFalse(StarPowerGenerator.HasStarPower(chart.PartOf(Guitar)!));
        Assert.IsNull(StarPowerGenerator.CommandOf(Guitar, []));
    }

    /// <summary>A guitar chart with <paramref name="notes"/> on Expert.</summary>
    internal static EditableChart Chart(IEnumerable<EditableNote> notes)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
        var list = notes.ToList();
        if (list.Count > 0)
            new AddNotes(Guitar, Expert, list).Apply(chart);
        return chart;
    }

    /// <summary>Quarter notes on green, red and yellow, the same difficulty throughout, over <paramref name="measures"/> measures from <paramref name="from"/>.</summary>
    internal static IEnumerable<EditableNote> Steady(long from, long measures)
    {
        for (long m = from; m < from + measures; m++)
        {
            for (int q = 0; q < 4; q++)
                yield return new EditableNote((m * Measure) + (q * Beat), 1, q % 3);
        }
    }

    /// <summary>Sixteenth notes and chords across the frets, far harder than <see cref="Steady"/>.</summary>
    private static IEnumerable<EditableNote> Hard(long from, long measures)
    {
        int[] lanes = [0, 2, 1, 3, 2, 4, 3, 1];
        int i = 0;
        for (long m = from; m < from + measures; m++)
        {
            for (int s = 0; s < 16; s++, i++)
            {
                long tick = (m * Measure) + (s * Beat / 4);
                int lane = lanes[i % lanes.Length];
                yield return new EditableNote(tick, 1, lane);
                if (s % 4 == 0)
                    yield return new EditableNote(tick, 1, lane == 4 ? 2 : 4);
            }
        }
    }

    /// <summary><paramref name="times"/> times eight measures of <see cref="Steady"/> then eight of <see cref="Hard"/>, choruses from measure 8, 24, 40….</summary>
    private static List<EditableNote> VerseChorus(int times)
    {
        var notes = new List<EditableNote>();
        for (int k = 0; k < times; k++)
        {
            notes.AddRange(Steady(16 * k, 8));
            notes.AddRange(Hard((16 * k) + 8, 8));
        }

        return notes;
    }

    private static StarPowerGeneration Propose(EditableChart chart, StarPowerOptions? options = null, (long From, long To)? span = null) =>
        StarPowerGenerator.Propose(chart, Guitar, options ?? StarPowerOptions.Default, span: span);

    private static List<(long Tick, EditableNote[] Notes)> Chords(EditableChart chart) =>
        [.. chart.PartOf(Guitar)![Expert].Notes.GroupBy(static n => n.Tick).OrderBy(static g => g.Key).Select(static g => (g.Key, g.ToArray()))];

    // The tick of the last chord a phrase covers on Expert, where a player completes it.
    private static long LastChordIn(EditableChart chart, Phrase phrase) =>
        chart.PartOf(Guitar)![Expert].Notes.Between(phrase.Tick, phrase.CoverEnd - 1)[^1].Tick;
}
