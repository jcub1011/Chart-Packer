using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Dedup;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using ChartPacker.Core.Tests.Isolation;
using ChartPacker.Core.Validation;
using static ChartBuilder.Tests.Charting.Saving.SaveFolders;

namespace ChartBuilder.Tests.Charting.Saving;

/// <summary>Chart Builder's save (charter-spec C17, §4.6) and a new chart's first save (C1), to songs on disk.</summary>
[TestClass]
public sealed class ChartSaveTests
{
    private const string DotChart = """
        [Song]
        {
          Name = "Converted"
          Artist = "Ignored, song.ini has one"
          Year = ", 2008"
          PreviewStart = 12.5
          Offset = 0.25
          Resolution = 192
        }
        [SyncTrack]
        {
          0 = TS 4
          0 = B 120000
        }
        [Events]
        {
        }
        [ExpertSingle]
        {
          0 = N 0 0
          192 = N 1 0
          384 = N 2 96
        }
        """;

    private SaveFolders _folders = null!;

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    public static IEnumerable<object[]> FixtureNames => MidiFixtures.Names;

    [TestInitialize]
    public void CreateFolders() => _folders = new SaveFolders();

    [TestCleanup]
    public void DeleteFolders() => _folders.Dispose();

    // AC C1.1–C1.3: a new chart's first save.

    [TestMethod]
    public async Task ANewChartWithOneStemSavesValidatesPacksAndReopensAsItWas()
    {
        // AC C1.1.
        string stem = _folders.Loose("Mix.WAV", Wav(3000));
        var song = NewSong([stem]);
        new AddTempo(new TempoChange(1920, EditableTempoMap.MicrosecondsPerQuarterOf(90))).Apply(song.Chart);
        song.Values = [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("artist", "Band")];
        song.DefaultCharter = "Me";
        song.AudioLengthMs = 3000;

        string folder = await SaveNewAsync(song, "Band - Song");

        Assert.AreSequenceEqual(new[] { "notes.mid", "song.ini", "song.wav" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        var plan = await PackPlanner.PlanAsync(folder, cancellationToken: Token);
        Assert.IsFalse(plan.HasErrors, string.Join("; ", plan.Findings.Select(f => $"{f.RuleId} {f.Message}")));
        var packed = await Packer.PackAsync(plan, Path.Combine(_folders.Root, "Band - Song.sng"), overwrite: false, cancellationToken: Token);
        Assert.DoesNotContain(f => f.Severity == Severity.Error, packed);

        var reopened = await OpenAsync(folder, Token);
        Assert.AreSequenceEqual(song.Chart.TempoMap.Tempos.ToList(), [.. reopened.Chart.TempoMap.Tempos]);
        Assert.AreSequenceEqual(song.Chart.Parts.Select(p => p.Instrument).ToList(), [.. reopened.Chart.Parts.Select(p => p.Instrument)]);
        Assert.AreEqual("Song", reopened.Value("name"));
        Assert.AreEqual("Band", reopened.Value("artist"));
        Assert.AreEqual("Me", reopened.Value("charter"));
        Assert.AreEqual("-1", reopened.Value("diff_guitar"));
        Assert.AreEqual("3000", reopened.Value("song_length"));
        Assert.AreEqual("Band – Song", song.Title, "The editor goes on with the song as opened from its folder.");
        Assert.IsFalse(song.IsNew);
        Assert.IsEmpty(song.PendingChanges(), "What was pending is in song.ini now.");
    }

    [TestMethod]
    public async Task CancellingAFirstSaveOrFailingToCopyAStemLeavesNothingBehind()
    {
        // AC C1.2.
        string parent = Directory.CreateDirectory(Path.Combine(_folders.Root, "Library")).FullName;
        string stem = _folders.Loose("guitar.ogg", Wav(500));
        var song = NewSong([stem]);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());
        var written = ChartSaver.Check(request);
        var stems = song.Tracks.Select(t => new NewChartStem(t.StagedSourcePath!, t.Stem)).ToList();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ChartSaver.SaveNewAsync(new NewSongTarget(parent, "Cancelled"), request, written, stems, cancelled.Token));

        File.Delete(stem);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ChartSaver.SaveNewAsync(new NewSongTarget(parent, "Missing stem"), request, written, stems, Token));

        Assert.IsEmpty(Directory.GetFileSystemEntries(parent), "No song folder, and no temp folder.");
    }

    [TestMethod]
    public async Task AFirstSaveNeverUsesOrTouchesAnExistingFolder()
    {
        // AC C1.3.
        string existing = _folders.Song("Taken", ("notes.mid", [1, 2, 3]), ("keep.txt", "mine"u8.ToArray()));
        var before = FolderSnapshot.Take(existing);
        var song = NewSong([]);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());

        await Assert.ThrowsAsync<IOException>(() => ChartSaver.SaveNewAsync(new NewSongTarget(_folders.Root, "Taken"), request, ChartSaver.Check(request), [], Token));

        Assert.IsEmpty(before.DiffTo(FolderSnapshot.Take(existing)));
        Assert.HasCount(1, Directory.GetDirectories(_folders.Root), "No temp folder was left beside it.");
    }

    // AC C2.1–C2.3 and C2.6: saving a song that was opened.

    [TestMethod]
    [DynamicData(nameof(FixtureNames))]
    public async Task SavingAnOpenedSongWithoutEditsReadsBackTheSameBothWays(string name)
    {
        // AC C2.1, through the save to disk.
        var fixture = MidiFixtures.Named(name);
        string folder = _folders.Song(name, ("notes.mid", fixture.Midi), ("song.ini", Ini(IniLinesOf(fixture.Options))));
        var opened = await OpenAsync(folder, Token);
        var song = EditorSong.Of(opened);
        var original = opened.Chart.Clone();

        await SaveAsync(song, Token);

        byte[] saved = await File.ReadAllBytesAsync(Path.Combine(folder, "notes.mid"), Token);
        var savedOptions = ChartReadOptions.From(SongIni.Read(Path.Combine(folder, "song.ini"), Token).Pairs);
        var instruments = original.Parts.Select(p => p.Instrument).ToList();
        var games = ChartReadBack.Compare(MidiChartReader.Read(fixture.Midi, fixture.Options), MidiChartReader.Read(saved, savedOptions), instruments);
        Assert.IsEmpty(games, GameViewTests.Describe(games));

        var reload = MidiChartLoader.Load(saved, savedOptions);
        var model = ChartModelComparer.Differences(MidiChartWriter.Normalize(original), reload.Chart);
        Assert.IsEmpty(model, string.Join("; ", model.Select(d => d.Description)));

        var chunks = MidiFile.ReadChunks(saved).Chunks.Select(c => Convert.ToHexString(saved[c.Start..c.End])).ToHashSet();
        foreach (var passthrough in original.Passthrough)
            Assert.Contains(Convert.ToHexString(passthrough.Chunk.Span), chunks, "Every passthrough track is written byte for byte.");
        Assert.AreSequenceEqual(original.EventsRaw.ToList(), [.. reload.Chart.EventsRaw]);
        foreach (var part in original.Parts)
            Assert.AreSequenceEqual(part.RawEvents.ToList(), [.. reload.Chart.PartOf(part.Instrument)!.RawEvents], $"{part.Instrument}");
    }

    [TestMethod]
    public async Task ImportingANotesChartAndSavingCarriesItsSongValuesAndReplacesIt()
    {
        // AC C2.2.
        string folder = _folders.Song("Imported", ("notes.chart", Encoding.UTF8.GetBytes(DotChart)), ("song.ini", Ini("artist = Kept")));
        var opened = await OpenAsync(folder, Token);
        var song = EditorSong.Of(opened);
        var before = MidiChartReader.Read(MidiChartWriter.Write(opened.Chart), opened.Options);

        await SaveAsync(song, Token, removeChart: opened.ChartName);

        Assert.AreSequenceEqual(new[] { "notes.mid", "song.ini" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray(), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        var pairs = SongIni.Read(Path.Combine(folder, "song.ini"), Token).Pairs;
        Assert.AreEqual("Converted", SongIniValuesOf(pairs, "name"));
        Assert.AreEqual("Kept", SongIniValuesOf(pairs, "artist"), "A value song.ini has is kept.");
        Assert.AreEqual("2008", SongIniValuesOf(pairs, "year"));
        Assert.AreEqual("12500", SongIniValuesOf(pairs, "preview_start_time"));
        Assert.AreEqual("250", SongIniValuesOf(pairs, "delay"), "The .chart's Offset is the delay now.");

        var chartRead = ChartTextReader.Read(Encoding.UTF8.GetBytes(DotChart));
        var saved = MidiChartReader.Read(await File.ReadAllBytesAsync(Path.Combine(folder, "notes.mid"), Token), ChartReadOptions.From(pairs));
        var differences = ChartReadBack.Compare(chartRead, saved, [ChartInstrument.Guitar]);
        Assert.IsEmpty(differences, GameViewTests.Describe(differences));
        Assert.IsEmpty(ChartReadBack.Compare(before, saved, [ChartInstrument.Guitar]));
    }

    [TestMethod]
    public async Task SavingASngKeepsItsMaskAndEveryOtherFile()
    {
        // AC C2.3.
        string midi = _folders.Loose("notes.mid", MidiFixtures.LeadGuitar());
        string audio = _folders.Loose("song.ogg", [1, 2, 3, 4, 5]);
        string art = _folders.Loose("album.png", [9, 8, 7]);
        string sng = Path.Combine(_folders.Root, "Song.sng");
        byte[] mask = [.. Enumerable.Range(1, SngMask.MaskSize).Select(b => (byte)(b * 7))];
        await SngWriter.WriteAsync(
            sng, mask, [KeyValuePair.Create("name", "Packed"), KeyValuePair.Create("diff_guitar", "3")],
            [new SngSourceFile("notes.mid", midi, new FileInfo(midi).Length), new SngSourceFile("song.ogg", audio, 5), new SngSourceFile("album.png", art, 3)],
            overwrite: false, cancellationToken: Token);
        var opened = await OpenAsync(sng, Token);
        var song = EditorSong.Of(opened);
        var track = song.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(48_000, 1, 2)]).Apply(song.Chart);

        await SaveAsync(song, Token);

        byte[] header = (await File.ReadAllBytesAsync(sng, Token))[..SngReader.HeaderSize];
        Assert.AreSequenceEqual(mask, header[(SngReader.HeaderSize - SngMask.MaskSize)..]);
        var entry = SongOpener.EntryOf(sng)!;
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4, 5 }, await ReadAsync(entry, "song.ogg", Token));
        Assert.AreSequenceEqual(new byte[] { 9, 8, 7 }, await ReadAsync(entry, "album.png", Token));
        var reopened = await OpenAsync(sng, Token);
        Assert.IsTrue(reopened.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].TryGetNote(48_000, 2, false, out _), "The edit was saved.");
        Assert.AreEqual(track.Notes.Count, reopened.Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.Count);
    }

    [TestMethod]
    [DataRow(SongEntryKind.SongFolder)]
    [DataRow(SongEntryKind.SngFile)]
    public async Task ASaveChangesTheSongsStampAndItsSummaryShowsTheSavedChart(SongEntryKind kind)
    {
        // AC C2.6: what Chart Packer re-reads a song by when its window is activated, and what it reads then.
        string path = kind == SongEntryKind.SongFolder
            ? _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")))
            : await PackedAsync("Song", MidiFixtures.LeadGuitar());
        var stamp = EntryStamps.Capture(path, kind, Token);
        var song = EditorSong.Of(await OpenAsync(path, Token));
        new AddPart(ChartInstrument.Bass).Apply(song.Chart);
        new AddNotes(ChartInstrument.Bass, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(song.Chart);

        await SaveAsync(song, Token);

        Assert.AreNotEqual(stamp, EntryStamps.Capture(path, kind, Token));
        var summary = await SongSummaryReader.ReadAsync(SongOpener.EntryOf(path)!, Token);
        Assert.AreEqual("-1", SongIniValuesOf(summary.Pairs, "diff_bass"));
        Assert.IsTrue(summary.Chart!.Instruments.HasFlag(ChartPacker.Core.Library.LibraryInstruments.Bass));
    }

    // AC C17.1–C17.4.

    [TestMethod]
    public async Task AKillAtAnyStepOfAFolderSaveLeavesTheOldChartOrTheWholeNewOne()
    {
        // AC C17.1, and AC C2.4 for an import's first save: what a kill at each step would leave in the folder.
        string folder = _folders.Song("Song", ("notes.chart", Encoding.UTF8.GetBytes(DotChart)), ("song.ini", Ini("name = Song")));
        byte[] oldIni = await File.ReadAllBytesAsync(Path.Combine(folder, "song.ini"), Token);
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges(), "notes.chart");
        var written = ChartSaver.Check(request);
        var states = new List<(string Step, bool HasChart, byte[]? Midi, byte[] Ini)>();

        SongFolderFiles.StepReached.Value = step => states.Add((
            step,
            File.Exists(Path.Combine(folder, "notes.chart")) || File.Exists(Path.Combine(folder, "notes.mid")),
            File.Exists(Path.Combine(folder, "notes.mid")) ? File.ReadAllBytes(Path.Combine(folder, "notes.mid")) : null,
            File.ReadAllBytes(Path.Combine(folder, "song.ini"))));
        try
        {
            await ChartSaver.SaveAsync(song.Entry!, request, written, Token);
        }
        finally
        {
            SongFolderFiles.StepReached.Value = null;
        }

        Assert.AreSequenceEqual(new[] { "staged", "moved:notes.mid", "before-deletes" }, [.. states.Select(s => s.Step)]);
        Assert.IsTrue(states.All(s => s.HasChart), "There's always a chart in the folder.");
        Assert.IsNull(states[0].Midi, "Before the move, the folder is as it was.");
        Assert.AreSequenceEqual(oldIni, states[0].Ini);
        Assert.AreSequenceEqual(written.Bytes, states[1].Midi!, "Once moved, the new notes.mid is whole.");
        Assert.AreSequenceEqual(oldIni, states[1].Ini, "song.ini is written after the chart is in place...");
        Assert.AreNotEqual(Convert.ToHexString(oldIni), Convert.ToHexString(states[2].Ini), "...and before the old chart is deleted.");
    }

    [TestMethod]
    public async Task AKillDuringAMidiSaveLeavesThePreviousNotesMidIntact()
    {
        // AC C17.1: the old notes.mid is replaced only by a rename of a whole new one.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new EditableNote(96_000, 1, 1)]).Apply(song.Chart);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());
        var written = ChartSaver.Check(request);
        byte[]? atStaged = null;
        SongFolderFiles.StepReached.Value = step =>
        {
            if (step == "staged")
                atStaged = File.ReadAllBytes(Path.Combine(folder, "notes.mid"));
        };
        try
        {
            await ChartSaver.SaveAsync(song.Entry!, request, written, Token);
        }
        finally
        {
            SongFolderFiles.StepReached.Value = null;
        }

        Assert.AreSequenceEqual(MidiFixtures.LeadGuitar(), atStaged!, "Up to the rename, the old notes.mid is there, whole.");
        Assert.AreSequenceEqual(written.Bytes, await File.ReadAllBytesAsync(Path.Combine(folder, "notes.mid"), Token));
    }

    [TestMethod]
    public async Task ASaveThatCantCopyItsFilesLeavesSongIniAsItWas()
    {
        // AC C17.1 and C17.3: song.ini is written only once the files are in place.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")));
        var before = FolderSnapshot.Take(folder);
        var draft = new MetadataDraft();
        draft.Set("name", "Changed");
        draft.AddFile(new AddedFile("notes.mid", Path.Combine(_folders.Root, "gone.mid"), 10));

        await Assert.ThrowsAsync<FileNotFoundException>(() => DraftWriter.SaveAsync(SongOpener.EntryOf(folder)!, draft, Token));

        Assert.IsEmpty(before.DiffTo(FolderSnapshot.Take(folder)));
    }

    [TestMethod]
    [DataRow(nameof(MidiFixtures.LeadGuitar))]
    [DataRow(nameof(MidiFixtures.ForcingAndTaps))]
    [DataRow(nameof(MidiFixtures.ProDrums))]
    [DataRow(nameof(MidiFixtures.SixFret))]
    [DataRow(nameof(MidiFixtures.TempoMap))]
    [DataRow(nameof(MidiFixtures.MultiplierNote103))]
    [DataRow(nameof(MidiFixtures.SongIniSettings))]
    public async Task ARandomlyEditedChartSavedToDiskReadsBackAsTheEditorShowsIt(string name)
    {
        // AC C17.2 and C17.4: the saved notes.mid read as the games read it gives the editor's game view, and loaded
        // again gives the model.
        var fixture = MidiFixtures.Named(name);
        string folder = _folders.Song(name, ("notes.mid", fixture.Midi), ("song.ini", Ini(IniLinesOf(fixture.Options))));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        var stack = new ChartUndoStack(song.Chart);
        var random = new CommandSequenceTests.RandomCommands(new Random(11));
        for (int done = 0, tries = 0; done < 150 && tries < 1500; tries++)
        {
            if (random.Next(song.Chart) is not { } command)
                continue;
            try
            {
                stack.Execute(command);
                done++;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                // Refused, as the generator doesn't look ahead; a refusal changes nothing.
            }
        }

        var request = await SaveAsync(song, Token);

        byte[] saved = await File.ReadAllBytesAsync(Path.Combine(folder, "notes.mid"), Token);
        var options = ChartReadOptions.From(SongIni.Read(Path.Combine(folder, "song.ini"), Token).Pairs);
        var shown = GameView.Project(MidiChartWriter.Normalize(request.Snapshot), MidiChartWriter.SettingsAfterSave(request.Snapshot, options));
        var games = ChartReadBack.Compare(shown, ChartReader.Read(saved, "notes.mid", options), song.Chart.Parts.Select(p => p.Instrument));
        Assert.IsEmpty(games, GameViewTests.Describe(games));
        var model = ChartModelComparer.Differences(MidiChartWriter.Normalize(request.Snapshot), MidiChartLoader.Load(saved, options).Chart);
        Assert.IsEmpty(model, string.Join("; ", model.Select(d => d.Description)));
    }

    [TestMethod]
    public async Task SavingChangesOnlyTheKeysPendingAndLeavesTheRestOfSongIniAsItWas()
    {
        // AC C17.3: comments, unknown keys, other sections and duplicates outside the pending keys are kept byte for byte.
        string ini = "; a comment\r\n[song]\r\nname = Song\r\nunknown_key = kept\r\nartist = A\r\nartist = B\r\nmultiplier_note = 103\r\n\r\n[other]\r\nx = 1\r\n";
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.MultiplierNote103()), ("song.ini", Encoding.UTF8.GetBytes(ini)));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        song.DefaultCharter = "Me";
        song.AudioLengthMs = 90_000;
        new AddPart(ChartInstrument.Bass).Apply(song.Chart);
        song.Revert("song_length");
        var pending = song.PendingChanges();

        await SaveAsync(song, Token);

        Assert.AreSequenceEqual(new[] { "charter", "diff_bass", "multiplier_note" }, [.. pending.Select(c => c.Key)]);
        string after = await File.ReadAllTextAsync(Path.Combine(folder, "song.ini"), Token);
        Assert.AreEqual(
            "; a comment\r\n[song]\r\nname = Song\r\nunknown_key = kept\r\nartist = A\r\nartist = B\r\ncharter = Me\r\ndiff_bass = -1\r\n\r\n[other]\r\nx = 1\r\n",
            after);
    }

    [TestMethod]
    public async Task SavingASngChangesOnlyThePendingKeys()
    {
        // AC C17.3, for a .sng's metadata.
        string sng = await PackedAsync("Song", MidiFixtures.LeadGuitar(), [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("loading_phrase", "kept")]);
        var song = EditorSong.Of(await OpenAsync(sng, Token));
        new AddPart(ChartInstrument.Bass).Apply(song.Chart);

        await SaveAsync(song, Token);

        var metadata = SngReader.Read(sng, Token).Metadata.Select(p => (p.Key, p.Value)).ToList();
        Assert.AreSequenceEqual(new[] { ("name", "Song"), ("loading_phrase", "kept"), ("diff_bass", "-1") }, [.. metadata]);
    }

    [TestMethod]
    public async Task AnchorsSurviveASaveAPackAndAReopen()
    {
        // AC C4.4.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.TempoMap()), ("song.ini", Ini("name = Song")));
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        var map = song.Chart.TempoMap;
        Assert.IsNotEmpty(map.Anchors, "The fixture has anchors.");
        var tempos = map.Tempos.ToList();
        var signatures = map.TimeSignatures.ToList();
        var anchors = map.Anchors.ToList();

        await SaveAsync(song, Token);
        var plan = await PackPlanner.PlanAsync(folder, cancellationToken: Token);
        string sng = Path.Combine(_folders.Root, "Packed.sng");
        await Packer.PackAsync(plan, sng, overwrite: false, cancellationToken: Token);
        var reopened = (await OpenAsync(sng, Token)).Chart.TempoMap;

        Assert.AreSequenceEqual(tempos, [.. reopened.Tempos]);
        Assert.AreSequenceEqual(signatures, [.. reopened.TimeSignatures]);
        Assert.AreSequenceEqual(anchors, [.. reopened.Anchors]);
    }

    [TestMethod]
    public async Task ANewChartInASongWithoutOneAddsNotesMidAndItsKeys()
    {
        // charter-spec C1, New chart in this song.
        string folder = _folders.Song("Song", ("song.ini", Ini("name = Song")), ("guitar.ogg", [1, 2, 3]));
        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(folder)!, Token);
        Assert.AreEqual(SongOpenProblemKind.NoChart, result.Problem!.Kind);
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        var song = EditorSong.Of(SongOpener.NewChartIn(result.Empty!, chart));

        Assert.AreSequenceEqual(new[] { "diff_guitar" }, [.. song.PendingChanges().Select(c => c.Key)]);
        await SaveAsync(song, Token);

        var reopened = await OpenAsync(folder, Token);
        Assert.AreEqual("notes.mid", reopened.ChartName);
        Assert.AreEqual("-1", reopened.Value("diff_guitar"));
        Assert.AreEqual("Song", reopened.Value("name"));
    }

    [TestMethod]
    public async Task ExportCopyWritesTheSavedSongElsewhereAndLeavesTheSongAlone()
    {
        // charter-spec C17 item 4.
        string folder = _folders.Song("Song", ("notes.mid", MidiFixtures.LeadGuitar()), ("song.ini", Ini("name = Song")), ("song.ogg", [1, 2]));
        var before = FolderSnapshot.Take(folder);
        var song = EditorSong.Of(await OpenAsync(folder, Token));
        new AddPart(ChartInstrument.Bass).Apply(song.Chart);
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());
        string target = Path.Combine(_folders.Root, "Exports", "Song");

        var result = await ChartSaver.ExportAsync(song.Entry!, request, ChartSaver.Check(request), target, Token);

        Assert.IsTrue(result.Extracted);
        Assert.IsEmpty(before.DiffTo(FolderSnapshot.Take(folder)));
        var copy = await OpenAsync(target, Token);
        Assert.IsNotNull(copy.Chart.PartOf(ChartInstrument.Bass));
        Assert.AreEqual("-1", copy.Value("diff_bass"));
        Assert.IsTrue(File.Exists(Path.Combine(target, "song.ogg")));
    }

    private static EditorSong NewSong(IReadOnlyList<string> stems)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));
        return EditorSong.New(chart, NewChartStems.TracksOf(stems).Tracks);
    }

    private async Task<string> SaveNewAsync(EditorSong song, string folderName)
    {
        var request = ChartSaver.RequestFor(song, song.Chart.Clone(), song.PendingChanges());
        var written = ChartSaver.Check(request);
        Assert.IsTrue(written.IsValid, Describe(written));
        var stems = song.Tracks.Select(t => new NewChartStem(t.StagedSourcePath!, t.Stem)).ToList();
        string folder = await ChartSaver.SaveNewAsync(new NewSongTarget(_folders.Root, folderName), request, written, stems, Token);
        var entry = SongOpener.EntryOf(folder)!;
        song.Saved(ChartSaver.SavedSong(song, entry, request), song.Chart.Parts.Select(p => p.Instrument).ToHashSet());
        return folder;
    }

    private async Task<string> PackedAsync(string name, byte[] midi, IReadOnlyList<KeyValuePair<string, string>>? metadata = null)
    {
        string source = _folders.Loose(name + ".mid", midi);
        string sng = Path.Combine(_folders.Root, name + ".sng");
        await SngWriter.WriteAsync(
            sng, SngMask.Generate(), metadata ?? [KeyValuePair.Create("name", name)],
            [new SngSourceFile("notes.mid", source, midi.Length)], overwrite: false, cancellationToken: Token);
        return sng;
    }

    private static string? SongIniValuesOf(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
        pairs.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
}
