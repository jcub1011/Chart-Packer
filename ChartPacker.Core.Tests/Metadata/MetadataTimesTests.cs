using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class MetadataTimesTests
{
    [TestMethod]
    [DataRow(0L, "00:00:00:000")]
    [DataRow(7L, "00:00:00:007")]
    [DataRow(225_123L, "00:03:45:123")]
    [DataRow(3_600_000L, "01:00:00:000")]
    [DataRow(360_000_000L, "100:00:00:000")]
    [DataRow(-150L, "-00:00:00:150")]
    [DataRow(long.MinValue, "-2562047788015:12:55:808")]
    public void FormatsMilliseconds(long ms, string expected) =>
        Assert.AreEqual(expected, MetadataTimes.FormatMilliseconds(ms));

    [TestMethod]
    [DataRow("song_length", "225123", "00:03:45:123")]
    [DataRow("SONG_LENGTH", "+1000", "00:00:01:000")]
    [DataRow("preview_start_time", "55000", "00:00:55:000")]
    [DataRow("preview_end_time", "85000", "00:01:25:000")]
    [DataRow("delay", "-150", "-00:00:00:150")]
    [DataRow("video_start_time", "-2500", "-00:00:02:500")]
    [DataRow("video_end_time", "61000", "00:01:01:000")]
    [DataRow("preview", "55000 85000", "00:00:55:000 – 00:01:25:000")]
    public void FormatsTimeKeys(string key, string value, string expected)
    {
        Assert.IsTrue(MetadataTimes.TryFormat(key, value, out string? formatted));
        Assert.AreEqual(expected, formatted);
    }

    [TestMethod]
    [DataRow("song_length", "-1")]
    [DataRow("video_end_time", "-1")]
    [DataRow("song_length", "3:45")]
    [DataRow("song_length", "1.5")]
    [DataRow("song_length", " 1000")]
    [DataRow("song_length", "")]
    [DataRow("preview", "55000")]
    [DataRow("preview", "55000 -1")]
    [DataRow("diff_guitar", "3")]
    [DataRow("sustain_cutoff_threshold", "320")]
    public void LeavesOtherValuesAsStored(string key, string value)
    {
        Assert.IsFalse(MetadataTimes.TryFormat(key, value, out string? formatted));
        Assert.IsNull(formatted);
    }
}
