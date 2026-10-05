using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;

namespace ChartPacker.Core.Tests.Validation;

[TestClass]
public sealed class QuickFixTests
{
    [TestMethod]
    [DataRow("1", "True")]
    [DataRow("true", "True")]
    [DataRow("TRUE", "True")]
    [DataRow("0", "False")]
    [DataRow("FALSE", "False")]
    public void ALenientBooleanIsWrittenExactly(string value, string expected)
    {
        var fix = Single([P("pro_drums", value)], SngRules.Sng060);

        Assert.AreSequenceEqual([new MetadataEdit("pro_drums", expected)], [.. fix.Edits]);
    }

    [TestMethod]
    public void AnEmptyValueIsRemoved()
    {
        var fix = Single([P("album", "")], SngRules.Sng061);

        Assert.AreSequenceEqual([new MetadataEdit("album", null)], [.. fix.Edits]);
    }

    [TestMethod]
    [DataRow("a;b\r\nc", "ab c")]
    [DataRow("one\n\ntwo", "one two")]
    [DataRow("a\0b", "ab")]
    [DataRow(" line\r\n", "line")]
    public void ForbiddenCharactersAreStripped(string value, string expected)
    {
        var fix = Single([P("name", value)], SngRules.Sng021);

        Assert.AreSequenceEqual([new MetadataEdit("name", expected)], [.. fix.Edits]);
    }

    [TestMethod]
    public void AValueOfOnlyForbiddenCharactersIsRemoved()
    {
        var fix = Single([P("name", ";\r\n")], SngRules.Sng021);

        Assert.AreSequenceEqual([new MetadataEdit("name", null)], [.. fix.Edits]);
    }

    [TestMethod]
    public void ALegacyKeyIsRenamedKeepingItsValue()
    {
        var fix = Single([P("Frets", "Someone")], SngRules.Sng025);

        Assert.AreSequenceEqual([new MetadataEdit("Frets", null), new MetadataEdit("charter", "Someone")], [.. fix.Edits]);
    }

    [TestMethod]
    public void ALegacyKeyIsntRenamedOntoAKeyTheSongAlreadyHas()
    {
        IReadOnlyList<KeyValuePair<string, string>> pairs = [P("frets", "Someone"), P("Charter", "Someone else")];

        Assert.DoesNotContain(f => f.RuleId == SngRules.Sng025, QuickFixRegistry.ProposeAll(pairs));
    }

    [TestMethod]
    public void FindingsWithoutAStrategyHaveNoFix()
    {
        IReadOnlyList<KeyValuePair<string, string>> pairs = [P("diff_guitar", "hard"), P("pro_drums", "yes")];

        Assert.IsEmpty(QuickFixRegistry.ProposeAll(pairs));
        Assert.IsNull(QuickFixRegistry.Propose(new Finding(SngRules.Sng024, Severity.Warning, "", Key: "diff_guitar"), pairs));
    }

    [TestMethod]
    public void TheRegistryKnowsWhichRulesItFixes()
    {
        Assert.IsTrue(QuickFixRegistry.IsSupported(SngRules.Sng060));
        Assert.IsTrue(QuickFixRegistry.IsSupported(SngRules.Sng061));
        Assert.IsTrue(QuickFixRegistry.IsSupported(SngRules.Sng021));
        Assert.IsTrue(QuickFixRegistry.IsSupported(SngRules.Sng025));
        Assert.IsTrue(QuickFixRegistry.IsSupported(SngRules.Sng022));
        Assert.IsFalse(QuickFixRegistry.IsSupported(SngRules.Sng024));
    }

    [TestMethod]
    public void ARepeatedSongIniKeyIsSetOnceToTheValueItHas()
    {
        var fix = QuickFixRegistry.ProposeAll([P("name", "B")], ["name"]).Single(f => f.RuleId == SngRules.Sng022);

        Assert.AreSequenceEqual([new MetadataEdit("name", "B")], [.. fix.Edits]);
    }

    [TestMethod]
    public void AnSngsRepeatsThatAgreeAreSetOnce()
    {
        var fix = Single([P("name", "A"), P("Name", "A")], SngRules.Sng022);

        Assert.AreSequenceEqual([new MetadataEdit("name", "A")], [.. fix.Edits]);
    }

    [TestMethod]
    public void AnSngsRepeatsThatDifferHaveNoFix() =>
        Assert.DoesNotContain(f => f.RuleId == SngRules.Sng022, QuickFixRegistry.ProposeAll([P("name", "A"), P("Name", "B")]));

    [TestMethod]
    public void FixingARepeatedSongIniKeyClearsItAndSavesItOnce()
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("name", "A")];
        var item = new MetadataItem(original, new MetadataDraft(), ["name"]);

        Assert.AreEqual(1, QuickFixRegistry.FixKey(item, "name"));

        var findings = MetadataValidator.Validate(item.Draft.ApplyTo(original), item.RemainingDuplicateKeys());
        Assert.DoesNotContain(f => f.RuleId == SngRules.Sng022, findings);
        Assert.AreEqual("[song]\nname = A\n", SongIni.ApplyEdits("[song]\nname = B\nname = A\n", item.Draft));
    }

    [TestMethod]
    public void AFindingForAKeyThatIsntThereHasNoFix()
    {
        var finding = new Finding(SngRules.Sng060, Severity.Warning, "", Key: "pro_drums");

        Assert.IsNull(QuickFixRegistry.Propose(finding, [P("name", "A")]));
    }

    [TestMethod]
    [DataRow("pro_drums", "1", SngRules.Sng060)]
    [DataRow("album", "", SngRules.Sng061)]
    [DataRow("name", "a;b", SngRules.Sng021)]
    [DataRow("frets", "Someone", SngRules.Sng025)]
    public void ApplyingAFixClearsItsFindingAndTouchesNothingElse(string key, string value, string ruleId)
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("artist", "B"), P(key, value), P("genre", "rock")];
        var item = new MetadataItem(original, new MetadataDraft());

        Single(original, ruleId).ApplyTo(item);

        var fixedPairs = item.Draft.ApplyTo(original);
        Assert.DoesNotContain(f => f.RuleId == ruleId, MetadataValidator.Validate(fixedPairs));
        CollectionAssert.IsSubsetOf(new[] { P("artist", "B"), P("genre", "rock") }, fixedPairs.ToArray());
        Assert.DoesNotContain(c => c.Key is "artist" or "genre", item.Draft.Changes);
    }

    [TestMethod]
    public void FixingADraftBackToTheSavedValueRevertsIt()
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("pro_drums", "True")];
        var draft = new MetadataDraft();
        draft.Set("pro_drums", "1");
        var item = new MetadataItem(original, draft);

        Single(draft.ApplyTo(original), SngRules.Sng060).ApplyTo(item);

        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void FixingAKeyAppliesEachOfItsFixesInTurn()
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("frets", "a;b")];
        var item = new MetadataItem(original, new MetadataDraft());

        int applied = QuickFixRegistry.FixKey(item, "FRETS");

        Assert.AreEqual(2, applied);
        Assert.AreSequenceEqual([P("charter", "ab")], [.. item.Draft.ApplyTo(original)]);
    }

    [TestMethod]
    public void FixingAKeyWithoutHintsLeavesALegacyKey()
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("frets", "a;b")];
        var item = new MetadataItem(original, new MetadataDraft());

        QuickFixRegistry.FixKey(item, "frets", includeHints: false);

        Assert.AreSequenceEqual([P("frets", "ab")], [.. item.Draft.ApplyTo(original)]);
    }

    [TestMethod]
    public void FixingAKeyWithNothingToFixLeavesTheDraftEmpty()
    {
        IReadOnlyList<KeyValuePair<string, string>> original = [P("diff_guitar", "hard"), P("pro_drums", "1")];
        var item = new MetadataItem(original, new MetadataDraft());

        Assert.AreEqual(0, QuickFixRegistry.FixKey(item, "diff_guitar"));
        Assert.IsTrue(item.Draft.IsEmpty);
    }

    private static QuickFix Single(IReadOnlyList<KeyValuePair<string, string>> pairs, string ruleId) =>
        QuickFixRegistry.ProposeAll(pairs).Single(f => f.RuleId == ruleId);

    private static KeyValuePair<string, string> P(string key, string value) => new(key, value);
}
