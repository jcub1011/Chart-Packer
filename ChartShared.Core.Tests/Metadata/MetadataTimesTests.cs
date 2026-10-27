using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Metadata;

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
    [DataRow(0L, "0:00")]
    [DataRow(59_999L, "0:59")]
    [DataRow(225_123L, "3:45")]
    [DataRow(3_599_999L, "59:59")]
    [DataRow(3_723_000L, "1:02:03")]
    [DataRow(360_000_000L, "100:00:00")]
    public void FormatsDurations(long ms, string expected) =>
        Assert.AreEqual(expected, MetadataTimes.FormatDuration(ms));

    [TestMethod]
    public void NegativeDurationThrows() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MetadataTimes.FormatDuration(-1));

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

    [TestMethod]
    [DataRow("225123", false, 225_123L)]
    [DataRow("+1000", false, 1_000L)]
    [DataRow("0", false, 0L)]
    [DataRow("-150", true, -150L)]
    [DataRow("+1000", true, 1_000L)]
    [DataRow("00:03:45:123", false, 225_123L)]
    [DataRow("00:00:00:000", false, 0L)]
    [DataRow("3:45:123", false, 225_123L)]
    [DataRow("3:45", false, 225_000L)]
    [DataRow("3:45.123", false, 225_123L)]
    [DataRow("45.5", false, 45_500L)]
    [DataRow("45.05", false, 45_050L)]
    [DataRow("45.005", false, 45_005L)]
    [DataRow("1:02:03", false, 3_723_000L)]
    [DataRow("1:02:03.5", false, 3_723_500L)]
    [DataRow("-00:00:01:500", true, -1_500L)]
    [DataRow("  00:00:55:000  ", false, 55_000L)]
    public void ParsesTimecodes(string text, bool allowNegative, long expected)
    {
        Assert.IsTrue(MetadataTimes.TryParseTimecode(text, allowNegative, out long ms));
        Assert.AreEqual(expected, ms);
    }

    [TestMethod]
    [DataRow("-1", false)]
    [DataRow("-00:00:01:000", false)]
    [DataRow("", false)]
    [DataRow("   ", true)]
    [DataRow("abc", false)]
    [DataRow("12:34:56:789:0", false)]
    [DataRow("1::2", false)]
    [DataRow("12:60", false)]
    [DataRow("1:2:3:1000", false)]
    [DataRow("1:2:3:4.5", false)]
    [DataRow("45.1234", false)]
    [DataRow("999999999999999:00:00:000", false)]
    [DataRow("45:123", false)]
    [DataRow("99999999999999999999", false)]
    [DataRow("1:99999999999999999999", false)]
    public void RejectsBadTimecodes(string text, bool allowNegative) =>
        Assert.IsFalse(MetadataTimes.TryParseTimecode(text, allowNegative, out _));

    [TestMethod]
    [DataRow("preview_start_time", "00:00:55:000", "55000")]
    [DataRow("preview_start_time", "55000", "55000")]
    [DataRow("preview_start_time", "0:55", "55000")]
    [DataRow("preview_start_time", "0:55.000", "55000")]
    [DataRow("delay", "-00:00:00:150", "-150")]
    [DataRow("delay", "-150", "-150")]
    [DataRow("preview", "00:00:55:000 00:01:25:000", "55000 85000")]
    [DataRow("preview", "0:55 1:25", "55000 85000")]
    public void NormalizesTimeKeys(string key, string text, string expected)
    {
        Assert.IsTrue(MetadataTimes.TryNormalize(key, text, out string? normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("preview_start_time", "abc")]
    [DataRow("preview_start_time", "-5")]
    [DataRow("preview_start_time", "")]
    [DataRow("preview", "55000")]
    [DataRow("preview", "55000 -1")]
    [DataRow("name", "x")]
    public void RejectsBadTimeKeyText(string key, string text) =>
        Assert.IsFalse(MetadataTimes.TryNormalize(key, text, out _));

    [TestMethod]
    [DataRow("preview_start_time", "55000", "00:00:55:000")]
    [DataRow("preview", "55000 85000", "00:00:55:000 00:01:25:000")]
    public void ShowsEditableTimecodes(string key, string value, string expected)
    {
        Assert.IsTrue(MetadataTimes.TryGetEditable(key, value, out string? editable));
        Assert.AreEqual(expected, editable);
    }

    [TestMethod]
    [DataRow("preview_start_time", true, true)]
    [DataRow("preview_end_time", true, true)]
    [DataRow("song_length", true, true)]
    [DataRow("delay", true, true)]
    [DataRow("preview", true, false)]
    [DataRow("name", false, false)]
    public void RecognisesTimeKeys(string key, bool isTime, bool isSingle)
    {
        Assert.AreEqual(isTime, MetadataTimes.IsTimeKey(key));
        Assert.AreEqual(isSingle, MetadataTimes.IsSingleTimeKey(key));
    }
}
