using ChartShared.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ChartShared.Core.Tests.Sng;

[TestClass]
public sealed class SngEntryStreamTests
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 600).Select(i => (byte)(i * 7))];

    private string _path = null!;

    [TestInitialize]
    public void WriteSng()
    {
        var builder = new SngBuilder();
        byte[] masked = [.. Data];
        SngMask.Apply(masked, builder.Mask);
        builder.AddFile("other.bin", [1, 2, 3]).AddFile("data.bin", masked);

        _path = Path.Combine(Path.GetTempPath(), $"chartpacker-entrystream-{Guid.NewGuid():N}.sng");
        File.WriteAllBytes(_path, builder.Build());
    }

    [TestCleanup]
    public void DeleteSng() => File.Delete(_path);

    [TestMethod]
    public void ReadsTheUnmaskedData()
    {
        using var handle = Open(out var entry, out var mask);
        using var stream = new SngEntryStream(handle, entry, mask);

        var copy = new MemoryStream();
        stream.CopyTo(copy, bufferSize: 97);

        Assert.AreEqual(Data.Length, stream.Length);
        Assert.AreSequenceEqual(Data, copy.ToArray());
    }

    [TestMethod]
    public void SeekingKeepsTheMaskInStep()
    {
        using var handle = Open(out var entry, out var mask);
        using var stream = new SngEntryStream(handle, entry, mask);

        stream.Seek(-10, SeekOrigin.End);
        byte[] tail = new byte[20];
        int read = stream.Read(tail);

        Assert.AreEqual(10, read);
        Assert.AreSequenceEqual(Data[^10..], tail[..10]);

        stream.Position = 300;
        byte[] middle = new byte[5];
        stream.ReadExactly(middle);
        Assert.AreSequenceEqual(Data[300..305], middle);
    }

    [TestMethod]
    public void ReadingAtTheEndGivesNothing()
    {
        using var handle = Open(out var entry, out var mask);
        using var stream = new SngEntryStream(handle, entry, mask) { Position = Data.Length + 5 };

        Assert.AreEqual(0, stream.Read(new byte[4]));
    }

    [TestMethod]
    public void SeekingBeforeTheStartThrows()
    {
        using var handle = Open(out var entry, out var mask);
        using var stream = new SngEntryStream(handle, entry, mask);

        Assert.ThrowsExactly<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [TestMethod]
    public void IsReadOnly()
    {
        using var handle = Open(out var entry, out var mask);
        using var stream = new SngEntryStream(handle, entry, mask);

        Assert.IsFalse(stream.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.WriteByte(1));
    }

    private SafeFileHandle Open(out SngFileEntry entry, out byte[] mask)
    {
        var read = SngReader.Read(_path);
        entry = read.Files.Single(f => f.Name == "data.bin");
        mask = read.Header!.Mask.ToArray();
        return File.OpenHandle(_path);
    }
}
