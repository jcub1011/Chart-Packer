using ChartShared.Core.Validation;

namespace ChartShared.Core.Tests.Validation;

[TestClass]
public sealed class MetadataValidatorTests
{
    [TestMethod]
    [DataRow("song_length", "213000")]
    [DataRow("delay", "-150")]
    [DataRow("delay", "+20")]
    [DataRow("pro_drums", "True")]
    [DataRow("modchart", "False")]
    [DataRow("cassettecolor", "#FF8800")]
    [DataRow("cassettecolor", "#f80")]
    [DataRow("preview", "55000 85000")]
    [DataRow("real_guitar_tuning", "0 0 0 0 0 0")]
    [DataRow("real_bass_tuning", "-2 0 0 0 \"Drop D\"")]
    [DataRow("name", "Anything goes = here")]
    [DataRow("my_custom_key", "hard")]
    public void AcceptsValuesOfTheExpectedType(string key, string value)
    {
        Assert.IsEmpty(MetadataValidator.ValidatePair(key, value));
    }

    [TestMethod]
    [DataRow("diff_guitar", "hard")]
    [DataRow("song_length", "3:20")]
    [DataRow("song_length", "213000.0")]
    [DataRow("delay", "-")]
    [DataRow("pro_drums", "yes")]
    [DataRow("cassettecolor", "FF8800")]
    [DataRow("cassettecolor", "#GG8800")]
    [DataRow("cassettecolor", "#FF888")]
    [DataRow("preview", "55000")]
    [DataRow("preview", "55000 85000 1")]
    [DataRow("real_guitar_tuning", "0 0 0")]
    [DataRow("real_guitar_tuning", "0 0 0 0 0 0 0")]
    [DataRow("real_guitar_tuning", "0 0 0 0 \"Open")]
    public void ReportsATypeMismatchAsAWarning(string key, string value)
    {
        var finding = MetadataValidator.ValidatePair(key, value).Single();

        Assert.AreEqual(SngRules.Sng024, finding.RuleId);
        Assert.AreEqual(Severity.Warning, finding.Severity);
        Assert.AreEqual(key, finding.Key);
        Assert.IsTrue(MetadataValidator.IsFailing(finding));
    }

    [TestMethod]
    [DataRow("true", "True")]
    [DataRow("TRUE", "True")]
    [DataRow("1", "True")]
    [DataRow("false", "False")]
    [DataRow("0", "False")]
    public void ReportsALenientBoolean(string value, string canonical)
    {
        var finding = MetadataValidator.ValidatePair("pro_drums", value).Single();

        Assert.AreEqual(SngRules.Sng060, finding.RuleId);
        Assert.AreEqual(Severity.Warning, finding.Severity);
        Assert.Contains(canonical, finding.Message);
    }

    [TestMethod]
    public void ReportsForbiddenCharactersAsErrors()
    {
        var key = MetadataValidator.ValidatePair("na=me", "A").Single();
        var value = MetadataValidator.ValidatePair("name", "A;B").Single();

        Assert.AreEqual(SngRules.Sng020, key.RuleId);
        Assert.AreEqual(SngRules.Sng021, value.RuleId);
        Assert.AreEqual(Severity.Error, key.Severity);
        Assert.AreEqual(Severity.Error, value.Severity);
    }

    [TestMethod]
    public void ReportsAnEmptyKeyAndAnEmptyValue()
    {
        Assert.AreEqual(SngRules.Sng023, MetadataValidator.ValidatePair("", "A").Single().RuleId);

        var empty = MetadataValidator.ValidatePair("name", "").Single();
        Assert.AreEqual(SngRules.Sng061, empty.RuleId);
        Assert.IsTrue(MetadataValidator.IsFailing(empty));
    }

    [TestMethod]
    public void ReportsALegacyKeyAsAHintThatDoesntFail()
    {
        var finding = MetadataValidator.ValidatePair("frets", "Someone").Single();

        Assert.AreEqual(SngRules.Sng025, finding.RuleId);
        Assert.AreEqual(Severity.Info, finding.Severity);
        Assert.Contains("charter", finding.Message);
        Assert.IsFalse(MetadataValidator.IsFailing(finding));
    }

    [TestMethod]
    public void ReportsARepeatedKeyOnceIgnoringCase()
    {
        var findings = MetadataValidator.Validate([P("name", "A"), P("artist", "B"), P("NAME", "C")]);

        var duplicate = findings.Single();
        Assert.AreEqual(SngRules.Sng022, duplicate.RuleId);
        Assert.AreEqual(Severity.Error, duplicate.Severity);
        Assert.AreEqual("name", duplicate.Key);
    }

    [TestMethod]
    public void ReportsKeysASongIniReaderAlreadyMerged()
    {
        var findings = MetadataValidator.Validate([P("name", "C"), P("artist", "B")], ["Name"]);

        Assert.AreEqual("name", findings.Single(f => f.RuleId == SngRules.Sng022).Key);
    }

    [TestMethod]
    public void ChecksEveryPairAndNamesItsKey()
    {
        var findings = MetadataValidator.Validate([P("name", "Song"), P("diff_guitar", "hard"), P("pro_drums", "1"), P("frets", "X")]);

        Assert.AreSequenceEqual(
            new (string?, string)[] { ("diff_guitar", SngRules.Sng024), ("pro_drums", SngRules.Sng060), ("frets", SngRules.Sng025) }, [.. findings.Select(f => (f.Key, f.RuleId))]);
    }

    [TestMethod]
    public void KnownKeysMatchIgnoringCase()
    {
        Assert.AreEqual(SngRules.Sng024, MetadataValidator.ValidatePair("Diff_Guitar", "hard").Single().RuleId);
    }

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);
}
