using ChartPacker.Core.Text;

namespace ChartPacker.Core.Tests.Text;

[TestClass]
public sealed class RichTextTests
{
    private static readonly RichTextStyle Plain = default;

    [TestMethod]
    public void PlainTextIsOneUnstyledSpan()
    {
        CollectionAssert.AreEqual(new[] { new RichTextSpan("Vivv", Plain) }, Parse("Vivv"));
        Assert.IsFalse(RichText.HasMarkup("Vivv"));
    }

    [TestMethod]
    public void EmptyTextHasNoSpans() => Assert.IsEmpty(RichText.Parse(""));

    [TestMethod]
    public void UnclosedColorRunsToTheEnd()
    {
        CollectionAssert.AreEqual(
            new[] { new RichTextSpan("Vivv", Plain with { Color = 0xFFFDFD96 }) },
            Parse("<color=#FDFD96FF>Vivv"));
        Assert.IsTrue(RichText.HasMarkup("<color=#FDFD96FF>Vivv"));
    }

    [TestMethod]
    public void ColorStackRestoresOuterColor()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                new RichTextSpan("a", Plain with { Color = 0xFFFF0000 }),
                new RichTextSpan("b", Plain with { Color = 0xFF0000FF }),
                new RichTextSpan("c", Plain with { Color = 0xFFFF0000 }),
                new RichTextSpan("d", Plain),
            },
            Parse("<color=red>a<color=#00F>b</color>c</color>d"));
    }

    [TestMethod]
    [DataRow("#FFF", 0xFFFFFFFFu)]
    [DataRow("#F008", 0x88FF0000u)]
    [DataRow("#12AB34", 0xFF12AB34u)]
    [DataRow("#12AB3480", 0x8012AB34u)]
    [DataRow("\"yellow\"", 0xFFFFFF00u)]
    [DataRow("ORANGE", 0xFFFF8000u)]
    [DataRow("#FF0000\"", 0xFFFF0000u)]
    [DataRow("\"#FF0000", 0xFFFF0000u)]
    public void ParsesColorForms(string value, uint expected) =>
        Assert.AreEqual(expected, Parse($"<color={value}>x").Single().Style.Color);

    [TestMethod]
    public void RainbowNameWithStrayQuotesIsColoured()
    {
        const string text =
            "<color=#ff0000\">M<color=#bf0240\">r<color=#800480\">D<color=#4005bf\">r<color=#0007ff\">u<color=#3306cc\">m" +
            "<color=#660499\">m<color=#990366\">e<color=#cc0133\">r<color=#ff0000\">b<color=#bf0240\">o<color=#800480\">y" +
            "<color=#4005bf\">9<color=#0007ff\">7";

        var spans = Parse(text);
        Assert.AreEqual("MrDrummerboy97", string.Concat(spans.Select(s => s.Text)));
        Assert.HasCount(14, spans);
        Assert.IsTrue(spans.All(s => s.Style.Color is not null));
        Assert.AreEqual(0xFFFF0000u, spans[0].Style.Color);
        Assert.IsTrue(RichText.HasMarkup(text));
    }

    [TestMethod]
    [DataRow("<color=#GGG>x")]
    [DataRow("<color=#12345>x")]
    [DataRow("<color=chartreuse>x")]
    [DataRow("<color=>x")]
    public void InvalidColorTagIsShownAsText(string text)
    {
        CollectionAssert.AreEqual(new[] { new RichTextSpan(text, Plain) }, Parse(text));
        Assert.IsFalse(RichText.HasMarkup(text));
    }

    [TestMethod]
    public void BoldItalicUnderlineStrikethroughNest()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                new RichTextSpan("a", Plain with { Bold = true }),
                new RichTextSpan("b", Plain with { Bold = true, Italic = true, Underline = true, Strikethrough = true }),
                new RichTextSpan("c", Plain),
            },
            Parse("<B>a<i><u><s>b</s></u></i></b>c"));
    }

    [TestMethod]
    public void StrayClosingTagIsHarmless() =>
        CollectionAssert.AreEqual(new[] { new RichTextSpan("ab", Plain) }, Parse("a</b></color>b"));

    [TestMethod]
    public void LineBreakBecomesNewlineSpan() =>
        CollectionAssert.AreEqual(
            new[] { new RichTextSpan("one", Plain), new RichTextSpan("\n", Plain), new RichTextSpan("two", Plain) },
            Parse("one<br>two"));

    [TestMethod]
    public void OtherTextMeshProTagsAreRemoved() =>
        Assert.AreEqual("Big small", RichText.ToPlainText("<size=150%>Big</size> <sup>small</sup>"));

    [TestMethod]
    [DataRow("I <3 this")]
    [DataRow("a < b > c")]
    [DataRow("<unknown>tag</unknown>")]
    [DataRow("<>")]
    [DataRow("<b=1>x")]
    public void NonTagsStayLiteral(string text)
    {
        Assert.AreEqual(text, RichText.ToPlainText(text));
        Assert.IsFalse(RichText.HasMarkup(text));
    }

    [TestMethod]
    public void NoParseKeepsTagsLiteral()
    {
        CollectionAssert.AreEqual(
            new[] { new RichTextSpan("<b>not bold</b>", Plain), new RichTextSpan("bold", Plain with { Bold = true }) },
            Parse("<noparse><b>not bold</b></noparse><b>bold"));
    }

    private static RichTextSpan[] Parse(string text) => RichText.Parse(text).ToArray();
}
