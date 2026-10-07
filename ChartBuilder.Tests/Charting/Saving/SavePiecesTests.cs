using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.ChartSettingsCommandTests;

namespace ChartBuilder.Tests.Charting.Saving;

/// <summary>
/// The pieces a save is made of: the pending <c>song.ini</c> changes and their reasons, a <c>.chart</c>'s <c>[Song]</c>
/// values, save points on the undo stack, reverting, and <c>[end]</c> moved after the last note.
/// </summary>
[TestClass]
public sealed class SavePiecesTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    private static readonly IReadOnlySet<ChartInstrument> JustGuitar = new HashSet<ChartInstrument> { Guitar };

    // Pending changes (charter-spec C17 item 2).

    [TestMethod]
    public void AnEmptyCharterIsFilledFromTheDefaultCharterName()
    {
        var chart = NewChart(Guitar);

        var filled = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, []) { DefaultCharter = " Me " });
        var kept = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, [KeyValuePair.Create("charter", "Them")]) { DefaultCharter = "Me" });
        var frets = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, [KeyValuePair.Create("frets", "Them")]) { DefaultCharter = "Me" });
        var none = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, []));

        Assert.AreSequenceEqual([new PendingSongIniChange("charter", "Me", PendingChangeReason.Charter)], filled.ToList());
        Assert.IsEmpty(kept);
        Assert.IsEmpty(frets, "frets is the older name for the charter.");
        Assert.IsEmpty(none);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow(88_500L, true)]
    [DataRow(89_000L, false)]
    [DataRow(91_000L, false)]
    [DataRow(91_001L, true)]
    public void SongLengthIsCorrectedWhenItIsMoreThanASecondOff(long? saved, bool changed)
    {
        var chart = NewChart(Guitar);
        IReadOnlyList<KeyValuePair<string, string>> pairs = saved is { } ms ? [KeyValuePair.Create("song_length", ms.ToString(System.Globalization.CultureInfo.InvariantCulture))] : [];

        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, pairs) { AudioLengthMs = 90_000 });

        Assert.AreEqual(changed, changes.Any(c => c is { Key: "song_length", Value: "90000", Reason: PendingChangeReason.SongLength }));
        Assert.IsEmpty(PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, pairs)), "Nothing until the audio is measured.");
    }

    [TestMethod]
    [DataRow(new string[0], "250")]
    [DataRow(new[] { "delay", "0" }, "250")]
    [DataRow(new[] { "delay", "100" }, null)]
    [DataRow(new[] { "delay_seconds", "0.1" }, null)]
    public void AnImportsOffsetBecomesTheDelayWhenSongIniSetsNone(string[] pair, string? delay)
    {
        var chart = NewChart(Guitar);
        IReadOnlyList<KeyValuePair<string, string>> pairs = pair.Length == 0 ? [] : [KeyValuePair.Create(pair[0], pair[1])];
        var import = Import(TimeSpan.FromMilliseconds(250), new ChartReadOptions(), []);

        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, pairs) { Import = import });

        Assert.AreEqual(delay, changes.SingleOrDefault(c => c.Key == "delay")?.Value);
    }

    [TestMethod]
    public void AnImportsSongValuesAndItsSustainCutoffArePendingWhereSongIniHasNone()
    {
        var chart = NewChart(Guitar);
        var import = Import(null, new ChartReadOptions(SustainCutoff: 0), [KeyValuePair.Create("name", "Name"), KeyValuePair.Create("artist", "Artist")]);

        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, [KeyValuePair.Create("artist", "Kept")]) { Import = import });

        Assert.AreSequenceEqual(
            [
                new PendingSongIniChange("name", "Name", PendingChangeReason.ImportedValue),
                new PendingSongIniChange("sustain_cutoff_threshold", "0", PendingChangeReason.ImportSettings),
            ],
            changes.ToList());
        Assert.IsTrue(changes[0].CanRevert);
        Assert.IsFalse(changes[1].CanRevert, "The chart is read by it.");
    }

    [TestMethod]
    public void StarPowerOn103IsSavedOn116SoMultiplierNoteIsRemoved()
    {
        var chart = NewChart(Guitar);

        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, [KeyValuePair.Create("multiplier_note", "103"), KeyValuePair.Create("star_power_note", "103")]));

        Assert.AreSequenceEqual(
            [new PendingSongIniChange("multiplier_note", null, PendingChangeReason.MultiplierNote), new PendingSongIniChange("star_power_note", null, PendingChangeReason.MultiplierNote)],
            changes.ToList());
        Assert.IsFalse(changes[0].CanRevert);
        Assert.IsEmpty(PendingSongIniChanges.Apply(new[] { KeyValuePair.Create("multiplier_note", "103") }, changes));
    }

    [TestMethod]
    public void TheDrumKeysCanNotBeRevertedButTheRatingsCan()
    {
        var chart = NewChart(Guitar);
        new AddPart(ChartInstrument.Drums, DrumsVariant.FiveLane).Apply(chart);

        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(JustGuitar, []));

        Assert.IsTrue(changes.Single(c => c.Key == "diff_drums").CanRevert);
        Assert.IsFalse(changes.Single(c => c.Key == "five_lane_drums").CanRevert);
    }

    // The editor's song: reverting, and moving on after a save.

    [TestMethod]
    public void ARevertedChangeIsListedButLeftOutOfTheSaveUntilItIsRestored()
    {
        var song = EditorSong.New(NewChart(Guitar), []);
        int changed = 0;
        song.Changed += (_, _) => changed++;
        song.DefaultCharter = "Me";

        song.Revert("charter");
        Assert.IsTrue(song.AllPendingChanges().Any(c => c.Key == "charter"));
        Assert.IsFalse(song.PendingChanges().Any(c => c.Key == "charter"));
        Assert.IsFalse(song.EffectivePairs().Any(p => p.Key == "charter"));

        song.Restore("charter");
        Assert.IsTrue(song.PendingChanges().Any(c => c.Key == "charter"));
        Assert.AreEqual(3, changed);
    }

    [TestMethod]
    public void AChartReadByAChangeCanNotHaveItReverted()
    {
        var chart = NewChart(ChartInstrument.Drums);
        var song = EditorSong.New(chart, []);

        song.Revert("pro_drums");

        Assert.IsTrue(song.PendingChanges().Any(c => c.Key == "pro_drums"));
    }

    [TestMethod]
    public void RevertingAnImportsDelayMovesTheChartBackAgainstTheAudio()
    {
        var opened = new OpenedSong(
            new ChartPacker.Core.Scanning.SongEntry(ChartPacker.Core.Scanning.SongEntryKind.SongFolder, @"C:\Song", ".", "Song"),
            [],
            ["notes.chart"],
            "notes.chart",
            new ChartLoadResult(NewChart(Guitar), []),
            new ChartReadOptions(),
            TimeSpan.FromMilliseconds(250),
            Import(TimeSpan.FromMilliseconds(250), new ChartReadOptions(), []));
        var song = EditorSong.Of(opened);

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), song.Delay);
        song.Revert("delay");
        Assert.AreEqual(TimeSpan.Zero, song.Delay);
    }

    // The .chart's [Song] section.

    [TestMethod]
    public void ADotChartsSongSectionBecomesSongIniPairs()
    {
        const string chart = """
            [Song]
            {
              Name = "The Name"
              Artist = "The Artist"
              Album = ""
              Genre = "rock"
              Year = ", 2008"
              Charter = "Someone"
              PreviewStart = 12.25
              Difficulty = 4
              Offset = 0.5
              Resolution = 192
              MusicStream = "song.ogg"
              Name = "Second"
            }
            [SyncTrack]
            {
            }
            """;

        var pairs = ChartSongSection.Read(Encoding.UTF8.GetBytes(chart));

        Assert.AreSequenceEqual(
            new[]
            {
                KeyValuePair.Create("name", "The Name"),
                KeyValuePair.Create("artist", "The Artist"),
                KeyValuePair.Create("genre", "rock"),
                KeyValuePair.Create("charter", "Someone"),
                KeyValuePair.Create("year", "2008"),
                KeyValuePair.Create("preview_start_time", "12250"),
                KeyValuePair.Create("diff_band", "4"),
            },
            pairs.ToArray());
        Assert.IsEmpty(ChartSongSection.Read("[SyncTrack]\n{\n}\n"u8.ToArray()));
    }

    // Save points.

    [TestMethod]
    public void EditsMadeWhileASaveRunsStayUnsaved()
    {
        var stack = new ChartUndoStack(NewChart(Guitar));
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(0, 1, 0)]));
        var point = stack.Mark();
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(480, 1, 1)]));

        stack.MarkSaved(point);

        Assert.IsTrue(stack.IsDirty);
        stack.Undo();
        Assert.IsFalse(stack.IsDirty, "Back at the snapshot the save wrote.");
        stack.Redo();
        Assert.IsTrue(stack.IsDirty);
    }

    [TestMethod]
    public void ASavePointUndoneAndReplacedCanNotBeReached()
    {
        var stack = new ChartUndoStack(NewChart(Guitar));
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(0, 1, 0)]));
        var point = stack.Mark();
        stack.Undo();
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(960, 1, 2)]));

        stack.MarkSaved(point);

        Assert.IsTrue(stack.IsDirty);
        stack.Undo();
        Assert.IsTrue(stack.IsDirty);
    }

    [TestMethod]
    public void ASavePointUndoneButStillToRedoIsReachedByRedoing()
    {
        var stack = new ChartUndoStack(NewChart(Guitar));
        stack.Execute(new AddNotes(Guitar, Expert, [new EditableNote(0, 1, 0)]));
        var point = stack.Mark();
        stack.Undo();

        stack.MarkSaved(point);

        Assert.IsTrue(stack.IsDirty);
        stack.Redo();
        Assert.IsFalse(stack.IsDirty);
    }

    [TestMethod]
    public void ARecoveredChartStartsUnsaved()
    {
        var stack = new ChartUndoStack(NewChart(Guitar), startsSaved: false);

        Assert.IsTrue(stack.IsDirty);
        stack.MarkSaved();
        Assert.IsFalse(stack.IsDirty);
    }

    // [end] after the last note (CHR085).

    [TestMethod]
    public void EndIsMovedToTheMeasureAfterTheLastNotesEnd()
    {
        var chart = NewChart(Guitar);
        new AddNotes(Guitar, Expert, [new EditableNote(1920, 600, 0), new EditableNote(4000, 1, 1)]).Apply(chart);
        Assert.AreEqual(0, chart.Events.AsSpan().ToArray().Single(e => e.Text == "[end]").Tick, "A new chart's [end] starts at 0.");

        var move = ChartSaveFixups.MoveEnd(chart)!;
        move.Apply(chart);

        Assert.AreEqual(5760, chart.Events.AsSpan().ToArray().Single(e => e.Text == "[end]").Tick);
        Assert.IsNull(ChartSaveFixups.MoveEnd(chart), "Already after the last note.");
        move.Revert(chart);
        Assert.AreEqual(0, chart.Events.AsSpan().ToArray().Single(e => e.Text == "[end]").Tick);
    }

    [TestMethod]
    public void EndIsAddedWhenThereIsNoneAndLeftAloneWithoutNotes()
    {
        var chart = NewChart(Guitar);
        Assert.IsNull(ChartSaveFixups.MoveEnd(chart), "No notes, nothing to be after.");

        new RemoveTextEvent(null, TextEvent.Of(0, "[end]")).Apply(chart);
        new AddNotes(Guitar, Expert, [new EditableNote(1920, 1, 0)]).Apply(chart);
        ChartSaveFixups.MoveEnd(chart)!.Apply(chart);

        Assert.AreEqual(3840, chart.Events.AsSpan().ToArray().Single(e => e.Text == "[end]").Tick);
    }

    private static ChartImportResult Import(TimeSpan? offset, ChartReadOptions options, IReadOnlyList<KeyValuePair<string, string>> values) =>
        new(new ChartLoadResult(NewChart(Guitar), []), offset, options, [], values);
}
