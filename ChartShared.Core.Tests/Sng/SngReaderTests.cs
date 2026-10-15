using ChartShared.Core.Sng;
using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Sng;

[TestClass]
public sealed class SngReaderTests
{
    /// <summary>
    /// The worked example from sng-spec §11: mask <c>10 11 … 1F</c>, <c>name = Test</c>, and
    /// <c>notes.chart</c> containing <c>ABC</c> (stored masked as <c>51 52 53</c>). The format it
    /// follows is defined by mdsitton/SngFileFormat's README (see THIRD-PARTY-NOTICES.md).
    /// </summary>
    internal static readonly byte[] WorkedExample = Convert.FromHexString(
        "534E47504B4701000000101112131415" +
        "161718191A1B1C1D1E1F180000000000" +
        "00000100000000000000040000006E61" +
        "6D650400000054657374240000000000" +
        "000001000000000000000B6E6F746573" +
        "2E636861727403000000000000006E00" +
        "00000000000003000000000000005152" +
        "53");

    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-sng-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    [TestMethod]
    public void WorkedExampleReadsCleanly()
    {
        Assert.HasCount(113, WorkedExample);

        var result = Read(WorkedExample);

        Assert.IsEmpty(result.Findings);
        Assert.IsNotNull(result.Header);
        Assert.AreEqual(1u, result.Header.Version);
        Assert.AreSequenceEqual([.. Enumerable.Range(0x10, 16).Select(b => (byte)b)], result.Header.Mask.ToArray());
        Assert.AreEqual(new SngMetadataPair("name", "Test", 26 + 16), result.Metadata.Single());
        Assert.AreEqual(new SngFileEntry("notes.chart", 3, 110), result.Files.Single());
    }

    [TestMethod]
    public void BuilderReproducesWorkedExample()
    {
        byte[] built = new SngBuilder().AddMetadata("name", "Test").AddFile("notes.chart", [0x51, 0x52, 0x53]).Build();

        Assert.AreSequenceEqual(WorkedExample, built);
    }

    [TestMethod]
    public async Task ReadAsyncMatchesRead()
    {
        string path = Write(WorkedExample);

        var result = await SngReader.ReadAsync(path, TestContext.CancellationToken);

        Assert.AreEqual("Test", result.Metadata.Single().Value);
        Assert.AreEqual("notes.chart", result.Files.Single().Name);
    }

    [TestMethod]
    public void EmptyFilesAndSubfoldersAreFine()
    {
        var result = Read(new SngBuilder()
            .AddMetadata("name", "Song")
            .AddMetadata("artist", "Band")
            .AddFile("notes.mid", [1, 2, 3, 4])
            .AddFile("empty.txt", [])
            .AddFile("extra/readme.txt", [5, 6])
            .Build());

        Assert.IsEmpty(result.Findings);
        Assert.AreSequenceEqual(["notes.mid", "empty.txt", "extra/readme.txt"], [.. result.Files.Select(f => f.Name)]);
    }

    [TestMethod]
    public void Sng001_BadIdentifier() =>
        AssertOnly(Read(Valid(b => b.Identifier = "SNGPKX"u8.ToArray())), SngRules.Sng001);

    [TestMethod]
    public void Sng002_UnsupportedVersion()
    {
        var result = Read(Valid(b => b.Version = 2));

        AssertOnly(result, SngRules.Sng002);
        Assert.AreEqual(2u, result.Header!.Version);
    }

    [TestMethod]
    public void Sng003_FileShorterThanHeader()
    {
        AssertOnly(Read(WorkedExample[..10]), SngRules.Sng003);
        AssertOnly(Read([]), SngRules.Sng003);
    }

    [TestMethod]
    public void Sng003_MetadataLengthPastEnd() =>
        AssertOnly(Read(Valid(b => b.MetadataLen = 10_000)), SngRules.Sng003);

    [TestMethod]
    public void Sng003_FileRangePastEnd()
    {
        var result = Read(Valid(b => b.LengthOverrides[0] = 1_000));

        Assert.Contains(f => f.RuleId == SngRules.Sng003 && f.Severity == Severity.Error, result.Findings);
    }

    [TestMethod]
    public void Sng003_KeepsMetadataWhenIndexIsBroken()
    {
        var result = Read(Valid(b => b.FileMetaLen = ulong.MaxValue));

        AssertOnly(result, SngRules.Sng003);
        Assert.AreEqual("Test", result.Metadata.Single().Value);
        Assert.IsEmpty(result.Files);
    }

    [TestMethod]
    public void Sng010_MetadataLengthLargerThanEntries()
    {
        var result = Read(Valid(b => b.MetadataPadding = 4));

        AssertOnly(result, SngRules.Sng010);
        Assert.AreEqual(26L, result.Findings.Single().Offset);
    }

    [TestMethod]
    public void Sng010_FileIndexLengthSmallerThanEntries()
    {
        // One byte short: the last entry runs past the declared end. Later sections are then
        // misaligned too, so other findings follow; only SNG010 is asserted.
        var result = Read(Valid(b => b.FileMetaLen = 8 + 1 + 11 + 16 - 1));

        Assert.Contains(f => f.RuleId == SngRules.Sng010, result.Findings);
    }

    [TestMethod]
    public void Sng011_NegativeKeyLength() =>
        AssertOnly(Read(new SngBuilder().AddRawMetadata([], [], keyLen: -1).Build()), SngRules.Sng011);

    [TestMethod]
    public void Sng011_NegativeValueLength() =>
        AssertOnly(Read(new SngBuilder().AddRawMetadata("k"u8.ToArray(), [], valueLen: -5).Build()), SngRules.Sng011);

    [TestMethod]
    public void Sng012_MetadataCountCantFit() =>
        AssertOnly(Read(Valid(b => b.MetadataCount = 1_000_000)), SngRules.Sng012);

    [TestMethod]
    public void Sng012_FileCountCantFit() =>
        AssertOnly(Read(Valid(b => b.FileCount = ulong.MaxValue)), SngRules.Sng012);

    [TestMethod]
    public void Sng013_InvalidUtf8InKey()
    {
        var result = Read(new SngBuilder().AddRawMetadata([0xC3, 0x28], "v"u8.ToArray()).Build());

        AssertOnly(result, SngRules.Sng013);
        Assert.AreEqual("�(", result.Metadata.Single().Key);
    }

    [TestMethod]
    public void Sng013_NulInFilename() =>
        AssertOnly(Read(new SngBuilder().AddRawFile("a\0b"u8.ToArray(), [1]).Build()), SngRules.Sng013);

    [TestMethod]
    public void Sng040_RangeOutsideFileData() =>
        AssertOnly(Read(Valid(b => b.OffsetOverrides[0] = 0)), SngRules.Sng040);

    [TestMethod]
    public void Sng040_OverlappingRanges()
    {
        var result = Read(TwoFiles(b => b.OffsetOverrides[1] = DataStartOfTwoFiles + 1, extraData: 1));

        Assert.Contains(f => f.RuleId == SngRules.Sng040 && f.Message.Contains("overlap", StringComparison.Ordinal), result.Findings);
    }

    [TestMethod]
    public void Sng041_Gap()
    {
        var result = Read(TwoFiles(b => b.OffsetOverrides[1] = DataStartOfTwoFiles + 4, extraData: 1));

        CollectionAssert.Contains(RuleIds(result), SngRules.Sng041);
        Assert.IsFalse(result.HasErrors);
    }

    [TestMethod]
    public void Sng041_OutOfIndexOrder()
    {
        var result = Read(TwoFiles(b =>
        {
            b.OffsetOverrides[0] = DataStartOfTwoFiles + 3;
            b.OffsetOverrides[1] = DataStartOfTwoFiles;
        }));

        AssertOnly(result, SngRules.Sng041);
    }

    [TestMethod]
    public void Sng042_SumDoesntMatchFileDataLength() =>
        AssertOnly(Read(Valid(b =>
        {
            b.FileDataLen = 5;
            b.Trailing = [0, 0];
        })), SngRules.Sng042);

    [TestMethod]
    public void Sng043_TrailingBytes()
    {
        var result = Read(Valid(b => b.Trailing = [1, 2, 3, 4, 5]));

        AssertOnly(result, SngRules.Sng043);
        Assert.AreEqual(Severity.Warning, result.Findings.Single().Severity);
        Assert.AreEqual(113L, result.Findings.Single().Offset);
    }

    [TestMethod]
    public void TruncatedAtEveryLengthNeverThrows()
    {
        for (int length = 0; length < WorkedExample.Length; length++)
        {
            var result = Read(WorkedExample[..length]);
            Assert.IsTrue(result.HasErrors, $"Truncated to {length} bytes should have an error.");
        }
    }

    [TestMethod]
    public void RandomCorruptionNeverThrows()
    {
        var random = new Random(20260925);
        byte[] original = new SngBuilder()
            .AddMetadata("name", "Song")
            .AddMetadata("diff_guitar", "3")
            .AddFile("notes.chart", new byte[64])
            .AddFile("song.ogg", new byte[128])
            .Build();

        for (int i = 0; i < 2_000; i++)
        {
            byte[] bytes = (byte[])original.Clone();
            int flips = random.Next(1, 5);
            for (int j = 0; j < flips; j++)
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);

            _ = Read(bytes);
        }

        for (int i = 0; i < 200; i++)
        {
            byte[] bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);
            "SNGPKG\u0001\0\0\0"u8.ToArray().AsSpan(0, Math.Min(10, bytes.Length)).CopyTo(bytes);
            _ = Read(bytes);
        }
    }

    [TestMethod]
    public void SectionsCrossingTheEndOfThePrefixReadTheSame()
    {
        // With one pair "k" = V bytes, the index's length field is at 51 + V and the FileData's at 95 + V, so these
        // put every field after the metadata on each side of, and across, the end of the first read.
        for (int valueLength = SngReader.PrefixSize - 110; valueLength <= SngReader.PrefixSize - 40; valueLength++)
        {
            string value = new('v', valueLength);
            var result = Read(new SngBuilder().AddMetadata("k", value).AddFile("notes.chart", [1, 2, 3]).Build());

            Assert.IsEmpty(result.Findings, $"Value of {valueLength} bytes.");
            Assert.AreEqual(value, result.Metadata.Single().Value);
            Assert.AreEqual(new SngFileEntry("notes.chart", 3, (ulong)(103 + valueLength)), result.Files.Single());
        }
    }

    [TestMethod]
    public void MetadataLargerThanThePrefixReadsTheSame()
    {
        string value = new('v', 3 * SngReader.PrefixSize);
        var result = Read(new SngBuilder().AddMetadata("name", "Big").AddMetadata("lyrics", value).AddFile("notes.chart", [1, 2, 3]).Build());

        Assert.IsEmpty(result.Findings);
        Assert.AreSequenceEqual(["Big", value], [.. result.Metadata.Select(p => p.Value)]);
        Assert.AreEqual("notes.chart", result.Files.Single().Name);
    }

    [TestMethod]
    public void FileTruncatedPastThePrefixIsReported()
    {
        byte[] full = new SngBuilder().AddMetadata("k", new string('v', 2 * SngReader.PrefixSize)).AddFile("notes.chart", [1, 2, 3]).Build();

        AssertOnly(Read(full[..(SngReader.PrefixSize + 10)]), SngRules.Sng003);
    }

    [TestMethod]
    public void ReadingAHandleMatchesReadingThePathAndLeavesItOpen()
    {
        string path = Write(TwoFiles(_ => { }));
        var byPath = SngReader.Read(path, TestContext.CancellationToken);

        using var handle = File.OpenHandle(path);
        var byHandle = SngReader.Read(handle, TestContext.CancellationToken);

        Assert.AreSequenceEqual([.. byPath.Metadata], [.. byHandle.Metadata]);
        Assert.AreSequenceEqual([.. byPath.Files], [.. byHandle.Files]);
        Assert.IsEmpty(byHandle.Findings);
        Assert.IsFalse(handle.IsClosed);
        Assert.AreEqual(new FileInfo(path).Length, RandomAccess.GetLength(handle));
    }

    [TestMethod]
    public void ReadingAStreamMatchesReadingThePathAndLeavesItOpen()
    {
        byte[] bytes = TwoFiles(_ => { });
        var byPath = SngReader.Read(Write(bytes), TestContext.CancellationToken);

        using var stream = new MemoryStream(bytes);
        var byStream = SngReader.Read(stream, TestContext.CancellationToken);

        Assert.AreSequenceEqual([.. byPath.Metadata], [.. byStream.Metadata]);
        Assert.AreSequenceEqual([.. byPath.Files], [.. byStream.Files]);
        Assert.IsEmpty(byStream.Findings);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    public void ReadingAStreamPastTheFirstReadMatchesReadingThePath()
    {
        // The metadata is larger than the first read, so the rest of the stream is read where the sections are.
        string value = new('v', 2 * SngReader.PrefixSize);
        byte[] full = new SngBuilder().AddMetadata("k", value).AddFile("notes.chart", [1, 2, 3]).Build();

        var byStream = SngReader.Read(new MemoryStream(full), TestContext.CancellationToken);

        Assert.IsEmpty(byStream.Findings);
        Assert.AreEqual(value, byStream.Metadata.Single().Value);
        Assert.AreSequenceEqual([.. Read(full).Files], [.. byStream.Files]);
        AssertOnly(SngReader.Read(new MemoryStream(full[..(SngReader.PrefixSize + 10)]), TestContext.CancellationToken), SngRules.Sng003);
    }

    [TestMethod]
    public void AStreamThatCantSeekIsRefused()
    {
        using var stream = new NonSeekableStream(WorkedExample);

        Assert.ThrowsExactly<ArgumentException>(() => SngReader.Read(stream, TestContext.CancellationToken));
    }

    /// <summary>
    /// Where file data starts for <see cref="TwoFiles"/>: header 26, metadataLen 8, metadata
    /// 8 + (4 + 4 + 4 + 3), fileMetaLen 8, index 8 + 2 × (1 + 5 + 16), fileDataLen 8.
    /// </summary>
    private const ulong DataStartOfTwoFiles = 26 + 8 + 23 + 8 + 52 + 8;

    [TestMethod]
    public void TwoFilesLayoutMatchesDataStart() =>
        Assert.AreEqual(DataStartOfTwoFiles, Read(TwoFiles(_ => { })).Files[0].Offset);

    private static SngBuilder TwoFilesBuilder() =>
        new SngBuilder().AddMetadata("name", "Two").AddFile("a.bin", [1, 2, 3]).AddFile("b.bin", [4, 5, 6]);

    /// <summary>Two 3-byte files; <paramref name="extraData"/> grows the FileData section so moved files still fit.</summary>
    private static byte[] TwoFiles(Action<SngBuilder> configure, int extraData = 0)
    {
        var builder = TwoFilesBuilder();
        configure(builder);
        if (extraData > 0)
        {
            builder.FileDataLen = 6 + (ulong)extraData;
            builder.Trailing = new byte[extraData];
        }
        return builder.Build();
    }

    private static byte[] Valid(Action<SngBuilder> configure)
    {
        var builder = new SngBuilder().AddMetadata("name", "Test").AddFile("notes.chart", [0x51, 0x52, 0x53]);
        configure(builder);
        return builder.Build();
    }

    private SngReadResult Read(byte[] bytes) => SngReader.Read(Write(bytes), TestContext.CancellationToken);

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private string Write(byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, $"{Guid.NewGuid():N}.sng");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] RuleIds(SngReadResult result) => [.. result.Findings.Select(f => f.RuleId).Distinct()];

    private static void AssertOnly(SngReadResult result, string ruleId) =>
        Assert.AreSequenceEqual(
            [ruleId], RuleIds(result), string.Join(Environment.NewLine, result.Findings.Select(f => $"{f.RuleId}: {f.Message}")));

    public TestContext TestContext { get; set; }
}
