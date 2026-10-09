using System.Globalization;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;

namespace ChartBuilder.Tests.Charting.LintRules;

/// <summary>
/// The CHR rules (charter-spec §6) and their fixes: a clean chart triggers none, and a change to it triggers just the
/// rule it's for (AC C16.1).
/// </summary>
[TestClass]
public sealed class ChartRuleTests
{
    private const int Beat = 480;
    private const int Notes = 16;
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    private static readonly TimeSpan AudioLength = TimeSpan.FromSeconds(60);

    /// <summary>Each rule, and what it's given to trigger it.</summary>
    public static IEnumerable<object[]> Triggers =>
    [
        [ChrRules.Chr001, 1],
        [ChrRules.Chr002, 1],
        [ChrRules.Chr003, 3],
        [ChrRules.Chr004, 1],
        [ChrRules.Chr005, 1],
        [ChrRules.Chr020, 1],
        [ChrRules.Chr021, 1],
        [ChrRules.Chr022, 1],
        [ChrRules.Chr023, 1],
        [ChrRules.Chr024, 1],
        [ChrRules.Chr025, 1],
        [ChrRules.Chr026, 1],
        [ChrRules.Chr040, 1],
        [ChrRules.Chr041, 1],
        [ChrRules.Chr042, 1],
        [ChrRules.Chr043, 3],
        [ChrRules.Chr044, 1],
        [ChrRules.Chr045, 1],
        [ChrRules.Chr046, 1],
        [ChrRules.Chr048, 1],
        [ChrRules.Chr060, 1],
        [ChrRules.Chr061, 1],
        [ChrRules.Chr062, 1],
        [ChrRules.Chr063, 4],
        [ChrRules.Chr080, 1],
        [ChrRules.Chr081, 1],
        [ChrRules.Chr082, 1],
        [ChrRules.Chr083, 1],
        [ChrRules.Chr084, 1],
        [ChrRules.Chr085, 1],
        [ChrRules.Chr090, 1],
        [ChrRules.Chr091, 1],
        [ChrRules.Chr092, 1],
        [ChrRules.Chr093, 1],
        [ChrRules.Chr094, 1],
        [ChrRules.Chr095, 1],
    ];

    /// <summary>The rules that have a chart fix.</summary>
    public static IEnumerable<object[]> Fixable => QuickFixRegistry.ChartStrategies.Select(s => new object[] { s.RuleId });

    [TestMethod]
    public void ACleanChartTriggersNoRule()
    {
        var findings = new ChartLinter().LintAll(Clean().Input).Findings;

        Assert.IsEmpty(findings, string.Join("; ", findings.Select(Describe)));
    }

    [TestMethod]
    public void ACleanChartWithDrumsTriggersNoRule()
    {
        var findings = new ChartLinter().LintAll(Clean([], drums: true).Input).Findings;

        Assert.IsEmpty(findings, string.Join("; ", findings.Select(Describe)));
    }

    [TestMethod]
    public void ACleanChartWithVocalsTriggersNoRule()
    {
        var (chart, input) = Clean();
        AddVocals(chart, VocalTrack.Vocals, CleanVocalNotes, CleanVocalLyrics, CleanVocalPhrases);
        AddVocals(chart, VocalTrack.Harmony1, CleanVocalNotes, CleanVocalLyrics, CleanVocalPhrases);

        var findings = new ChartLinter().LintAll(Snapshot(chart, input)).Findings;

        Assert.IsEmpty(findings, string.Join("; ", findings.Select(Describe)));
    }

    [TestMethod]
    public void AVocalFindingNamesItsTrack()
    {
        var findings = new ChartLinter().LintAll(Triggered(ChrRules.Chr091).Input).Findings;

        Assert.AreEqual("PART VOCALS", findings.Single().Track);
        Assert.AreEqual(Beat, findings.Single().Tick);
    }

    [TestMethod]
    public void APartChartedOnOnlySomeDifficultiesIsFine()
    {
        // GitHub issue #53: a difficulty left empty isn't an issue; only a part with no notes at all is (CHR080).
        var (chart, input) = Clean([KeyValuePair.Create("diff_bass", "2")]);
        new AddPart(ChartInstrument.Bass).Apply(chart);
        new AddNotes(ChartInstrument.Bass, Expert, [new EditableNote(0, 1, 0)]).Apply(chart);

        var findings = new ChartLinter().LintAll(Snapshot(chart, input)).Findings;

        Assert.IsEmpty(findings, string.Join("; ", findings.Select(Describe)));
    }

    [TestMethod]
    public void EveryRuleHasATriggerHere()
    {
        Assert.AreSequenceEqual(
            ChartLintRules.All.Select(r => r.RuleId).ToList(), [.. Triggers.Select(t => (string)t[0])], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DynamicData(nameof(Triggers))]
    public void EachRuleIsTriggeredByItsChangeAndNothingElseIs(string ruleId, int count)
    {
        // AC C16.1.
        var findings = new ChartLinter().LintAll(Triggered(ruleId).Input).Findings;

        Assert.HasCount(count, findings, string.Join("; ", findings.Select(Describe)));
        Assert.IsTrue(findings.All(f => f.RuleId == ruleId), string.Join("; ", findings.Select(Describe)));
        Assert.IsTrue(findings.All(f => f.Location is not null || f.Tick is not null || f.Key is not null || f.Track is not null), "Every finding says where it is.");
    }

    [TestMethod]
    [DynamicData(nameof(Fixable))]
    public void EachFixClearsItsFindingAsOneStepAndUndoingItBringsItBack(string ruleId)
    {
        var (chart, input) = Triggered(ruleId);
        var linter = new ChartLinter();
        var before = linter.LintAll(input);
        var stack = new ChartUndoStack(chart);

        var fix = QuickFixRegistry.ProposeChartFix(before.FindingsOf(ruleId)[0], chart, input.Options);
        Assert.IsNotNull(fix);
        stack.Execute(fix);
        var fixedFindings = linter.LintAll(Snapshot(chart, input)).Findings;
        stack.Undo();
        var undone = linter.LintAll(Snapshot(chart, input));

        Assert.IsEmpty(fixedFindings, string.Join("; ", fixedFindings.Select(Describe)));
        Assert.AreEqual(1, stack.RedoCount, "One step.");
        Assert.AreSequenceEqual(before.Findings.Select(Describe).ToList(), [.. undone.Findings.Select(Describe)]);
    }

    [TestMethod]
    public void AFixWithNoSingleRightAnswerProposesNothing()
    {
        // A note snapped onto a note already on the grid.
        var (chart, input) = Clean();
        new AddNotes(Guitar, Expert, [new EditableNote(477, 1, 1)]).Apply(chart);
        var finding = new ChartLinter().LintAll(Snapshot(chart, input)).FindingsOf(ChrRules.Chr004).Single();

        Assert.IsNull(QuickFixRegistry.ProposeChartFix(finding, chart, input.Options));
    }

    [TestMethod]
    public void ARevertedSongLengthIsTheFixOfChr084()
    {
        var chart = Clean().Chart;
        var song = EditorSong.New(chart, []);
        song.AudioLengthMs = 60_000;
        song.Revert("song_length");
        var input = new ChartLintInput(chart.Clone(), song.EffectivePairs(), audioLength: AudioLength);
        var finding = new ChartLinter().LintAll(input).FindingsOf(ChrRules.Chr084).Single();

        Assert.AreEqual("song_length", PendingSongIniFixes.KeyToRestore(finding, song));
        song.Restore("song_length");
        Assert.IsEmpty(new ChartLinter().LintAll(new ChartLintInput(chart.Clone(), song.EffectivePairs(), audioLength: AudioLength)).FindingsOf(ChrRules.Chr084));
        Assert.IsNull(PendingSongIniFixes.KeyToRestore(finding, song), "Nothing left to restore.");
    }

    [TestMethod]
    public void Chr021TakesOnlyLengthsMeantAsSustains()
    {
        // A sixteenth is a plain note as written; a length from a sixteenth up to the cutoff (160 at 480) is flagged.
        var (chart, input) = Clean();
        var expert = chart.PartOf(Guitar)![Expert];
        new SetSustain(Guitar, Expert, [expert.Notes[0]], 119).Apply(chart);
        new SetSustain(Guitar, Expert, [expert.Notes[1]], 120).Apply(chart);
        new SetSustain(Guitar, Expert, [expert.Notes[2]], 160).Apply(chart);

        var findings = new ChartLinter().LintAll(Snapshot(chart, input)).FindingsOf(ChrRules.Chr021);

        Assert.AreSequenceEqual(new long[] { Beat }, [.. findings.Select(f => f.Location!.Tick)]);
    }

    [TestMethod]
    public void Chr023FlagsAForceOnlyWhenItChangesNothingAtEveryThreshold()
    {
        // 165 ticks apart is a HOPO at Rock Band's threshold (170) but not YARG's (161): neither force is redundant.
        var (chart, input) = Clean();
        new AddNotes(Guitar, Expert, [new EditableNote((5 * Beat) + 165, 1, 1)]).Apply(chart);
        new SetChordFlags(Guitar, Expert, [(5 * Beat) + 165], ChordFlags.ForceHopo, on: true).Apply(chart);

        var findings = new ChartLinter().LintAll(Snapshot(chart, input)).Findings;

        Assert.DoesNotContain(f => f.RuleId == ChrRules.Chr023, findings, string.Join("; ", findings.Select(Describe)));
    }

    /// <summary>
    /// A chart that triggers no rule: Lead guitar at 120 BPM in 4/4, each difficulty sparser than the one above it, a
    /// section, <c>[end]</c> after the last note, rated, and its <c>song_length</c> the audio's. With
    /// <paramref name="drums"/>, Pro drums too: red notes spaced as the guitar's, rated.
    /// </summary>
    private static (EditableChart Chart, ChartLintInput Input) Clean() => Clean([]);

    private static (EditableChart Chart, ChartLintInput Input) Clean(IReadOnlyList<KeyValuePair<string, string>> extraIni, TimeSpan? audio = null, bool drums = false)
    {
        var parts = new HashSet<ChartInstrument> { Guitar };
        if (drums)
            parts.Add(Drums);
        var chart = EditableChart.CreateNew(new NewChartSettings("", Beat, 120, 4, 4, parts, DrumsVariant.Pro));
        foreach (var part in chart.Parts)
        {
            foreach (var track in part.Tracks)
            {
                // Expert a note a beat, Hard every two, Medium every four, Easy every eight.
                int every = 1 << (3 - (int)track.Difficulty);
                var notes = Enumerable.Range(0, Notes / every).Select(i => new EditableNote((long)i * every * Beat, 1, part.Kind == ChartInstrumentKind.Drums ? 1 : i % 5)).ToList();
                new AddNotes(part.Instrument, track.Difficulty, notes).Apply(chart);
            }
        }

        new AddTextEvent(null, TextEvent.Of(0, "[section Intro]")).Apply(chart);
        ChartSaveFixups.MoveEnd(chart)!.Apply(chart);
        return (chart, Snapshot(chart, IniWith(extraIni, drums), audio ?? AudioLength));
    }

    private static IReadOnlyList<KeyValuePair<string, string>> IniWith(IReadOnlyList<KeyValuePair<string, string>> extra, bool drums = false)
    {
        var pairs = new List<KeyValuePair<string, string>> { KeyValuePair.Create("diff_guitar", "3"), KeyValuePair.Create("song_length", "60000") };
        if (drums)
            pairs.AddRange([KeyValuePair.Create("diff_drums", "3"), KeyValuePair.Create("diff_drums_real", "3"), KeyValuePair.Create("pro_drums", "True"), KeyValuePair.Create("five_lane_drums", "False")]);
        foreach (var pair in extra)
        {
            pairs.RemoveAll(p => p.Key == pair.Key);
            if (pair.Value.Length > 0)
                pairs.Add(pair);
        }

        return pairs;
    }

    /// <summary>The clean chart, changed to trigger <paramref name="ruleId"/>.</summary>
    private static (EditableChart Chart, ChartLintInput Input) Triggered(string ruleId)
    {
        var (chart, input) = ruleId switch
        {
            ChrRules.Chr003 => Clean([KeyValuePair.Create("song_length", "5000")], TimeSpan.FromSeconds(5)),
            ChrRules.Chr083 => Clean([KeyValuePair.Create("diff_guitar", "-1")]),
            ChrRules.Chr084 => Clean([KeyValuePair.Create("song_length", "58000")]),
            ChrRules.Chr040 or ChrRules.Chr041 or ChrRules.Chr042 or ChrRules.Chr043 or ChrRules.Chr044 or ChrRules.Chr045 or ChrRules.Chr046 or ChrRules.Chr048
                => Clean([], drums: true),
            _ => Clean(),
        };
        var map = chart.TempoMap;
        var expert = chart.PartOf(Guitar)![Expert];
        switch (ruleId)
        {
            case ChrRules.Chr001:
                map.TimeSignatures.RemoveAt(0);
                break;
            case ChrRules.Chr002:
                new AddTempo(new TempoChange(Notes * Beat, EditableTempoMap.MicrosecondsPerQuarterOf(500))).Apply(chart);
                break;
            case ChrRules.Chr004:
                new AddNotes(Guitar, Expert, [new EditableNote(247, 1, 2)]).Apply(chart);
                break;
            case ChrRules.Chr005:
                map.Tempos.Add(map.Tempos[0]);
                break;
            case ChrRules.Chr020:
                new SetSustain(Guitar, Expert, [expert.Notes[0]], 3000).Apply(chart);
                break;
            case ChrRules.Chr021:
                new SetSustain(Guitar, Expert, [expert.Notes[1]], 130).Apply(chart);
                break;
            case ChrRules.Chr022:
                new AddNotes(Guitar, Expert, [new EditableNote(0, 1, ChartNote.OpenLane)]).Apply(chart);
                break;
            case ChrRules.Chr023:
                new SetChordFlags(Guitar, Expert, [Beat], ChordFlags.ForceStrum, on: true).Apply(chart);
                break;
            case ChrRules.Chr024:
                new AddNotes(Guitar, Expert, [new EditableNote(Beat / 2, 1, 0)]).Apply(chart);
                new SetChordFlags(Guitar, Expert, [Beat / 2], ChordFlags.ForceHopo, on: true).Apply(chart);
                break;
            case ChrRules.Chr025:
                new SetChordFlags(Guitar, Expert, [0], ChordFlags.ForceHopo, on: true).Apply(chart);
                break;
            case ChrRules.Chr026:
                new AddNotes(Guitar, Expert, [new EditableNote(10, 1, 0)]).Apply(chart);
                break;
            case ChrRules.Chr040:
                chart.PartOf(Drums)!.TomMarkers.Add(new TomMarker(2, 3 * Beat, 1));
                break;
            case ChrRules.Chr041:
                new AddNotes(Drums, Expert, [new EditableNote(Beat, 1, 2), new EditableNote(Beat, 1, 3)]).Apply(chart);
                break;
            case ChrRules.Chr042:
                new AddPhrase(Drums, new Phrase(PhraseKind.StarPower, 8 * Beat, Beat)).Apply(chart);
                new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 8 * Beat, 1)).Apply(chart);
                break;
            case ChrRules.Chr043:
                // Only Expert has a note on the fill's last beat.
                new AddPhrase(Drums, new Phrase(PhraseKind.DrumFill, 8 * Beat, Beat + 1)).Apply(chart);
                break;
            case ChrRules.Chr044:
                new AddPhrase(Drums, new Phrase(PhraseKind.Roll, Beat, Beat / 2, Expert)).Apply(chart);
                break;
            case ChrRules.Chr045:
                new AddNotes(Drums, Expert, [new EditableNote(0, 1, 3)]).Apply(chart);
                new SetToms([(3, 0L)], tom: true).Apply(chart);
                new SetDrumsVariant(DrumsVariant.FourLane).Apply(chart);
                break;
            case ChrRules.Chr046:
                new AddNotes(Drums, Expert, [new EditableNote(Beat / 2, 1, 5)]).Apply(chart);
                break;
            case ChrRules.Chr048:
                chart.AddPassthrough(new PassthroughTrack(EditableChartSamples.VocalsChunk, "PART DRUMS_2X"));
                break;
            case ChrRules.Chr060:
                new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, Beat / 2, 10)).Apply(chart);
                break;
            case ChrRules.Chr061:
                new AddPhrase(Guitar, new Phrase(PhraseKind.Solo, Beat / 2, 10)).Apply(chart);
                break;
            case ChrRules.Chr062:
                new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 0, 2 * Beat)).Apply(chart);
                new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, Beat, 2 * Beat)).Apply(chart);
                break;
            case ChrRules.Chr063:
                new AddPhrase(Guitar, new Phrase(PhraseKind.BigRockEnding, 0, 8 * Beat)).Apply(chart);
                break;
            case ChrRules.Chr080:
                new AddPart(ChartInstrument.Bass).Apply(chart);
                break;
            case ChrRules.Chr081:
                new AddNotes(Guitar, ChartDifficulty.Easy, [.. Enumerable.Range(0, Notes * 2).Select(i => new EditableNote(((long)i * Beat / 2) + (Beat / 4), 1, 1))]).Apply(chart);
                break;
            case ChrRules.Chr082:
                new RemoveTextEvent(null, TextEvent.Of(0, "[section Intro]")).Apply(chart);
                break;
            case ChrRules.Chr090:
                AddVocals(chart, VocalTrack.Vocals, [new(0, Beat + 10, 60), new(Beat, 240, 62)], CleanVocalLyrics, CleanVocalPhrases);
                break;
            case ChrRules.Chr091:
                AddVocals(chart, VocalTrack.Vocals, CleanVocalNotes, [CleanVocalLyrics[0]], CleanVocalPhrases);
                break;
            case ChrRules.Chr092:
                AddVocals(chart, VocalTrack.Vocals, CleanVocalNotes, CleanVocalLyrics, [new(0, Beat - 1)]);
                break;
            case ChrRules.Chr093:
                AddVocals(chart, VocalTrack.Vocals, CleanVocalNotes, [.. CleanVocalLyrics, new(2 * Beat, "stray")], CleanVocalPhrases);
                break;
            case ChrRules.Chr094:
                AddVocals(chart, VocalTrack.Vocals, CleanVocalNotes, [new(0, "+"), CleanVocalLyrics[1]], CleanVocalPhrases);
                break;
            case ChrRules.Chr095:
                new AddVocalPart(VocalTrack.Harmony2).Apply(chart);
                break;
            case ChrRules.Chr085:
                var end = chart.Events.AsSpan().ToArray().Single(e => e.Text == ChartSaveFixups.EndText);
                new RemoveTextEvent(null, end).Apply(chart);
                break;
        }

        return (chart, Snapshot(chart, input));
    }

    // A vocal part that triggers no rule: two sung notes with their lyrics, in one phrase.
    private static readonly VocalNoteEvent[] CleanVocalNotes = [new(0, 240, 60), new(Beat, 240, 62)];
    private static readonly VocalLyricEvent[] CleanVocalLyrics = [new(0, "Hel-"), new(Beat, "lo")];
    private static readonly VocalPhraseEvent[] CleanVocalPhrases = [new(0, 4 * Beat)];

    private static void AddVocals(EditableChart chart, VocalTrack track, VocalNoteEvent[] notes, VocalLyricEvent[] lyrics, VocalPhraseEvent[] phrases)
    {
        new AddVocalPart(track).Apply(chart);
        var content = VocalTrackContent.Of(chart.Passthrough[VocalTracks.IndexIn(chart, track)]);
        new ChangeVocals(track, content.With(notes, lyrics, phrases)).Apply(chart);
    }

    private static ChartLintInput Snapshot(EditableChart chart, ChartLintInput settings) =>
        new(chart.Clone(), settings.SongIni, settings.Options, settings.AudioLength, settings.Delay);

    private static ChartLintInput Snapshot(EditableChart chart, IReadOnlyList<KeyValuePair<string, string>> songIni, TimeSpan audio) =>
        new(chart.Clone(), songIni, MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.From(songIni)), audio);

    private static string Describe(Finding f) =>
        string.Create(CultureInfo.InvariantCulture, $"{f.RuleId} {f.Location} {f.Track} {f.Tick} {f.Key}: {f.Message}");
}
