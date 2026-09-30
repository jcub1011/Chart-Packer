using ChartPacker.Core.Dedup;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using static ChartPacker.Core.Tests.Dedup.DedupFixtures;

namespace ChartPacker.Core.Tests.Dedup;

[TestClass]
public sealed class DedupAnalyzerTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-dedup-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public async Task AFolderAndTheSngPackedFromItAreExactCopies()
    {
        var folder = Song(_root, "Song");
        var sng = await PackAsync(folder, Path.Combine(_root.FullName, "Song.sng"));

        var analysis = await DedupAnalyzer.AnalyzeAsync([folder, sng]);

        AssertExact(analysis, folder, sng);
    }

    [TestMethod]
    public async Task TwoSngsWithDifferentMasksAndFileOrderAreExactCopies()
    {
        var folder = Song(_root, "Song");
        string chart = Path.Combine(folder.FullPath, "notes.chart");
        string audio = Path.Combine(folder.FullPath, "guitar.ogg");
        SngSourceFile[] files = [new("notes.chart", chart, new FileInfo(chart).Length), new("guitar.ogg", audio, new FileInfo(audio).Length)];
        KeyValuePair<string, string>[] metadata = [KeyValuePair.Create("name", "Title"), KeyValuePair.Create("artist", "Artist")];

        string first = Path.Combine(_root.FullName, "Song.sng");
        string second = Path.Combine(_root.FullName, "Song (1).sng");
        await SngWriter.WriteAsync(first, SngMask.Generate(), metadata, files, overwrite: false);
        await SngWriter.WriteAsync(second, SngMask.Generate(), [.. metadata.Reverse()], [.. files.Reverse()], overwrite: false);

        var analysis = await DedupAnalyzer.AnalyzeAsync([SngEntry(first), SngEntry(second), folder]);

        AssertExact(analysis, SngEntry(first), SngEntry(second), folder);
    }

    [TestMethod]
    public async Task JunkFilesAndNameCaseDontStopAnExactMatch()
    {
        var clean = Song(_root, "Clean");
        var sng = await PackAsync(clean, Path.Combine(_root.FullName, "Clean.sng"));
        var messy = Folder(
            _root,
            "Messy",
            IniFor("Artist", "Title"),
            ("NOTES.chart", Utf8(Chart)),
            ("Guitar.OGG", File.ReadAllBytes(Path.Combine(clean.FullPath, "guitar.ogg"))),
            ("desktop.ini", Utf8("junk")),
            (".DS_Store", Utf8("junk")),
            ("__MACOSX/._guitar.ogg", Utf8("junk")));

        var analysis = await DedupAnalyzer.AnalyzeAsync([sng, messy]);

        AssertExact(analysis, sng, messy);
    }

    [TestMethod]
    public async Task AFolderWithFilesAnSngCantHoldIsNeverAnExactCopy()
    {
        var folder = Song(_root, "Song");
        var sng = await PackAsync(folder, Path.Combine(_root.FullName, "Song.sng"));
        File.WriteAllBytes(Path.Combine(folder.FullPath, "old.sng"), [1, 2, 3]);

        var analysis = await DedupAnalyzer.AnalyzeAsync([folder, sng]);

        Assert.IsEmpty(analysis.Exact);
        var chart = analysis.SameChart.Single();
        var withExtras = chart.Entries.Single(e => e.Entry.Kind == SongEntryKind.SongFolder);
        CollectionAssert.AreEqual(new[] { "old.sng" }, withExtras.Extras.ToArray());
    }

    [TestMethod]
    public async Task SubfoldersAnSngAlsoHoldsMatch()
    {
        var folder = Folder(_root, "Song", IniFor("Artist", "Title"), ("notes.chart", Utf8(Chart)), ("stems/extra.ogg", Bytes(100, 2)));
        var sng = await PackAsync(folder, Path.Combine(_root.FullName, "Song.sng"));

        var analysis = await DedupAnalyzer.AnalyzeAsync([folder, sng]);

        AssertExact(analysis, folder, sng);
    }

    [TestMethod]
    public async Task KeyCaseDoesntMatterButValuesDo()
    {
        var lower = Folder(_root, "Lower", "[song]\nname = Title\nartist = Artist\n", ("notes.chart", Utf8(Chart)));
        var upper = Folder(_root, "Upper", "[song]\nName = Title\nARTIST = Artist\n", ("notes.chart", Utf8(Chart)));
        var retitled = Folder(_root, "Retitled", "[song]\nname = Title!\nartist = Artist\n", ("notes.chart", Utf8(Chart)));

        var analysis = await DedupAnalyzer.AnalyzeAsync([lower, upper, retitled]);

        AssertExact(analysis, lower, upper);
        Assert.HasCount(2, analysis.SameChart.Single().Variants);
    }

    [TestMethod]
    public async Task ALegacyCharterCreditIsntFoldedAway()
    {
        var both = Folder(_root, "Both", IniFor("Artist", "Title", "frets = Old\ncharter = New\n"), ("notes.chart", Utf8(Chart)));
        var one = Folder(_root, "One", IniFor("Artist", "Title", "charter = New\n"), ("notes.chart", Utf8(Chart)));

        var analysis = await DedupAnalyzer.AnalyzeAsync([both, one]);

        Assert.IsEmpty(analysis.Exact);
        Assert.HasCount(2, analysis.SameChart.Single().Variants);
    }

    [TestMethod]
    public async Task DifferentArtIsTheSameChartAndTheDifferenceIsDescribed()
    {
        byte[] audio = Bytes(4096, 7);
        var a = Folder(_root, "A", IniFor("Artist", "Title"), ("notes.chart", Utf8(Chart)), ("guitar.ogg", audio), ("album.png", Bytes(64, 1)));
        var b = Folder(_root, "B", IniFor("Artist", "Title"), ("notes.chart", Utf8(Chart)), ("guitar.ogg", audio), ("album.png", Bytes(64, 2)));

        var analysis = await DedupAnalyzer.AnalyzeAsync([a, b]);

        Assert.IsEmpty(analysis.Exact);
        var variants = analysis.SameChart.Single().Variants;
        var differences = await DedupDifferences.CompareAsync(variants[0].Entries[0], variants[1].Entries[0], analysis.Hasher);
        Assert.AreEqual(new FileDifference("album.png", FileDifferenceKind.Differs), differences.Files.Single());
        Assert.AreEqual(2, differences.IdenticalFiles);
        Assert.IsEmpty(differences.Metadata);
    }

    [TestMethod]
    public async Task DifferentChartsOfOneSongAreMergeCandidates()
    {
        var a = Song(_root, "A", "The Band", "Song");
        var b = Song(_root, "B", "the band", "Song", chart: Chart.Replace("0 = N 0 0", "0 = N 1 0"));
        var other = Song(_root, "C", "The Band", "Other Song", chart: Chart.Replace("0 = N 0 0", "0 = N 2 0"));

        var analysis = await DedupAnalyzer.AnalyzeAsync([a, b, other]);

        Assert.IsEmpty(analysis.Exact);
        Assert.IsEmpty(analysis.SameChart);
        var song = analysis.MergeCandidates.Single();
        CollectionAssert.AreEquivalent(new[] { a.FullPath, b.FullPath }, song.Charts.SelectMany(c => c.Entries).Select(e => e.Entry.FullPath).ToArray());
    }

    [TestMethod]
    public async Task SongsWithoutAChartCanStillBeExactCopies()
    {
        var a = Folder(_root, "A", IniFor("Artist", "Title"), ("guitar.ogg", Bytes(100, 1)));
        var b = Folder(_root, "B", IniFor("Artist", "Title"), ("guitar.ogg", Bytes(100, 1)));

        var analysis = await DedupAnalyzer.AnalyzeAsync([a, b]);

        AssertExact(analysis, a, b);
        Assert.IsEmpty(analysis.SameChart);
        Assert.IsEmpty(analysis.MergeCandidates);
    }

    [TestMethod]
    public async Task ADamagedSngIsSkipped()
    {
        string path = Path.Combine(_root.FullName, "Broken.sng");
        File.WriteAllBytes(path, Utf8("not an sng at all, but long enough to have a header"));

        var analysis = await DedupAnalyzer.AnalyzeAsync([SngEntry(path)]);

        Assert.AreEqual(DedupSkipReason.SngHasErrors, analysis.Skipped.Single().Reason);
    }

    [TestMethod]
    public async Task AudioIsntReadWhenTheChartsAlreadyDiffer()
    {
        var a = Song(_root, "A", audio: Bytes(1 << 20, 1));
        var b = Song(_root, "B", chart: Chart.Replace("0 = N 0 0", "0 = N 1 0"), audio: Bytes(1 << 20, 1));

        var analysis = await DedupAnalyzer.AnalyzeAsync([a, b]);

        Assert.IsEmpty(analysis.Exact);
        Assert.AreEqual(2, analysis.Hasher.FilesHashed);
        Assert.IsLessThan(1 << 20, analysis.Hasher.BytesHashed);
    }

    [TestMethod]
    public async Task ASongChangedWhileBeingComparedIsSkipped()
    {
        var a = Song(_root, "A");
        var b = Song(_root, "B");
        bool touched = false;
        var progress = new Inline<DedupProgress>(p =>
        {
            if (p.Phase == DedupPhase.Comparing && !touched)
            {
                touched = true;
                File.SetLastWriteTimeUtc(Path.Combine(b.FullPath, "guitar.ogg"), DateTime.UtcNow.AddMinutes(5));
            }
        });

        var analysis = await DedupAnalyzer.AnalyzeAsync([a, b], progress: progress);

        Assert.IsEmpty(analysis.Exact);
        var skipped = analysis.Skipped.Single();
        Assert.AreEqual(b.FullPath, skipped.Entry.FullPath);
        Assert.AreEqual(DedupSkipReason.ChangedDuringAnalysis, skipped.Reason);
    }

    [TestMethod]
    public async Task ProgressCountsEverySongRead()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Song(_root, "S" + i, title: "T" + i)).ToList();
        DedupProgress? last = null;

        await DedupAnalyzer.AnalyzeAsync(entries, progress: new Inline<DedupProgress>(p =>
        {
            if (p.Phase == DedupPhase.Reading)
                last = p;
        }));

        Assert.AreEqual(30, last!.ItemsDone);
        Assert.AreEqual(30, last.ItemsTotal);
    }

    [TestMethod]
    public async Task EachComparingStageKeepsItsTotal()
    {
        // C and D are exact copies; A and B share a chart under different titles.
        SongEntry[] entries =
        [
            Song(_root, "A", title: "One"),
            Song(_root, "B", title: "Two", audio: Bytes(4096, 2)),
            Song(_root, "C", title: "Three"),
            Song(_root, "D", title: "Three"),
        ];
        var reports = new List<DedupProgress>();

        await DedupAnalyzer.AnalyzeAsync(entries, progress: new Inline<DedupProgress>(reports.Add));

        foreach (var phase in new[] { DedupPhase.Comparing, DedupPhase.MatchingCharts })
        {
            var stage = reports.Where(p => p.Phase == phase).ToList();
            Assert.IsNotEmpty(stage);
            Assert.AreEqual(1, stage.Select(p => p.ItemsTotal).Distinct().Single());
            Assert.IsTrue(stage.All(p => p.ItemsDone <= p.ItemsTotal));
            Assert.AreEqual(stage[0].ItemsTotal, stage[^1].ItemsDone);
        }
    }

    [TestMethod]
    public async Task ACancelledHashIsNotRemembered()
    {
        var song = await FingerprintAsync();
        var hasher = new ContentHasher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => hasher.HashAsync(song, "guitar.ogg", cts.Token));

        Assert.AreEqual(Hash(Bytes(4096, 1)), await hasher.HashAsync(song, "guitar.ogg"));
    }

    [TestMethod]
    public async Task AFailedHashIsNotRemembered()
    {
        var song = await FingerprintAsync();
        var hasher = new ContentHasher();
        string audio = Path.Combine(song.Entry.FullPath, "guitar.ogg");
        DateTime written = File.GetLastWriteTimeUtc(audio);
        File.Move(audio, audio + ".away");

        await Assert.ThrowsAsync<SongChangedException>(() => hasher.HashAsync(song, "guitar.ogg"));

        File.Move(audio + ".away", audio);
        File.SetLastWriteTimeUtc(audio, written);
        Assert.AreEqual(Hash(Bytes(4096, 1)), await hasher.HashAsync(song, "guitar.ogg"));
    }

    [TestMethod]
    public async Task CancellingThrows()
    {
        var a = Song(_root, "A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => DedupAnalyzer.AnalyzeAsync([a], cancellationToken: cts.Token));
    }

    /// <summary>A fingerprinted song folder, from two exact copies so both are fingerprinted.</summary>
    private async Task<SongFingerprint> FingerprintAsync()
    {
        var analysis = await DedupAnalyzer.AnalyzeAsync([Song(_root, "A"), Song(_root, "B")]);
        return analysis.Exact.Single().Entries[0];
    }

    private static string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    private static void AssertExact(DedupAnalysis analysis, params SongEntry[] expected)
    {
        var variant = analysis.Exact.Single();
        CollectionAssert.AreEquivalent(expected.Select(e => e.FullPath).ToArray(), variant.Entries.Select(e => e.Entry.FullPath).ToArray());
    }
}
