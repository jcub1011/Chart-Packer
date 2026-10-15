using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;

namespace ChartBuilder.Tests.Charting;

/// <summary>The practice sections of <c>EVENTS</c> (charter-spec C8 item 3): read, named and renamed.</summary>
[TestClass]
public sealed class SectionEventsTests
{
    [TestMethod]
    [DataRow("[section Verse 1]", "Verse 1")]
    [DataRow("[SECTION  Chorus ]", "Chorus")]
    [DataRow("[prc_verse_1]", "verse 1")]
    [DataRow(" [prc_solo] ", "solo")]
    [DataRow("[section]", null)]
    [DataRow("[music_start]", null)]
    [DataRow("free text", null)]
    public void ASectionsNameIsReadFromEitherStyle(string text, string? name)
    {
        Assert.AreEqual(name, SectionEvents.NameOf(text));
        Assert.AreEqual(name is not null, SectionEvents.IsSection(text));
    }

    [TestMethod]
    [DataRow("Verse 2", null, "[section Verse 2]")]
    [DataRow(" Verse 2 ", "[section Verse 1]", "[section Verse 2]")]
    [DataRow("verse 2", "[prc_verse_1]", "[prc_verse_2]")]
    public void ASectionRenamedKeepsItsStyle(string name, string? styleOf, string text)
    {
        Assert.AreEqual(text, SectionEvents.TextFor(name, styleOf));
        Assert.AreEqual(name.Trim(), SectionEvents.NameOf(text));
    }

    [TestMethod]
    public void TheCommonNamesAreSectionsAndAllAreListedInOrder()
    {
        var chart = DrumCommandTests.DrumsChart();
        foreach (var (name, i) in SectionEvents.CommonNames.Select((n, i) => (n, i)))
            new AddTextEvent(null, TextEvent.Of(i * 1920L, SectionEvents.TextFor(name))).Apply(chart);

        var sections = SectionEvents.All(chart);

        Assert.AreSequenceEqual(SectionEvents.CommonNames, [.. sections.Select(e => SectionEvents.NameOf(e.Text)!)]);
    }

    [TestMethod]
    public void RenamingASectionIsOneChangeThatRevertsExactly()
    {
        var chart = DrumCommandTests.DrumsChart();
        var verse = TextEvent.Of(1920, "[prc_verse_1]");
        new AddTextEvent(null, verse).Apply(chart);

        var after = CommandChecks.AppliesRevertsAndRedoes(chart, new ChangeTextEvent(null, verse, verse with { Text = SectionEvents.TextFor("verse 2", verse.Text) }));

        Assert.AreEqual("verse 2", SectionEvents.NameOf(SectionEvents.All(after).Single().Text));
    }
}
