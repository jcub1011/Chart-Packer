using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class KnownKeysTests
{
    [TestMethod]
    public void KeysAreUniqueIgnoringCase()
    {
        var keys = KnownKeys.All.Select(k => k.Key).ToList();

        Assert.HasCount(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void KeysAreLowercaseAndStorable()
    {
        foreach (var known in KnownKeys.All)
        {
            Assert.AreEqual(known.Key.ToLowerInvariant(), known.Key);
            Assert.IsTrue(MetadataRules.CanRoundTrip(known.Key, "x"), known.Key);
        }
    }

    [TestMethod]
    public void EveryKeyHasADescription()
    {
        foreach (var known in KnownKeys.All)
            Assert.IsFalse(string.IsNullOrWhiteSpace(known.Description), known.Key);
    }

    [TestMethod]
    public void CommonKeysComeFirst()
    {
        Assert.AreSequenceEqual(
            ["name", "artist", "album", "genre", "year", "charter", "song_length", "preview_start_time", "delay"], [.. KnownKeys.All.Take(9).Select(k => k.Key)]);
        Assert.StartsWith("diff_", KnownKeys.All[9].Key);
    }

    [TestMethod]
    public void LegacyKeysAreLeftOutAndPointAtKnownKeys()
    {
        foreach (var (legacy, replacement) in KnownKeys.LegacyReplacements)
        {
            Assert.IsFalse(KnownKeys.TryGet(legacy, out _), legacy);
            Assert.IsTrue(KnownKeys.TryGet(replacement, out _), replacement);
        }
    }

    [TestMethod]
    public void LookupIgnoresCase()
    {
        Assert.IsTrue(KnownKeys.TryGet("Diff_Guitar", out var known));
        Assert.AreEqual(new KnownKey("diff_guitar", KnownKeyType.Integer), known);
        Assert.IsFalse(KnownKeys.TryGet("my_custom_key", out _));
    }

    [TestMethod]
    public void MillisecondKeysAreIntegers()
    {
        foreach (string key in new[] { "song_length", "preview_start_time", "preview_end_time", "delay", "video_start_time", "video_end_time" })
        {
            Assert.IsTrue(KnownKeys.TryGet(key, out var known), key);
            Assert.AreEqual(KnownKeyType.Integer, known.Type, key);
        }
    }
}
