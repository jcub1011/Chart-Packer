using ChartBuilder.Controls;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Where the timeline's lyric lens is along the song's lyrics for a time under the pointer: "Hel" sung from 1 s to 2 s and
/// laid out from 0 to 30, then "lo" from 3 s to 4 s laid out from 30 to 50, joined to it, then "there" from 6 s to 6 s,
/// laid out from 60 to 100.
/// </summary>
[TestClass]
public sealed class LyricLensTests
{
    private static readonly OverviewLyric[] Lyrics =
    [
        new(1, 2, "Hel", JoinsNext: true, EndsLine: false),
        new(3, 4, "lo", JoinsNext: false, EndsLine: false),
        new(6, 6, "there", JoinsNext: false, EndsLine: true),
    ];

    private static readonly double[] Lefts = [0, 30, 60];
    private static readonly double[] Rights = [30, 50, 100];

    [TestMethod]
    [DataRow(0.0, 0.0, DisplayName = "Before the first syllable, at its start")]
    [DataRow(1.0, 0.0, DisplayName = "At a syllable's start")]
    [DataRow(1.5, 15.0, DisplayName = "Halfway through a syllable, halfway through its text")]
    [DataRow(2.5, 30.0, DisplayName = "Between two joined syllables, where they meet")]
    [DataRow(3.5, 40.0, DisplayName = "Halfway through the second")]
    [DataRow(5.0, 55.0, DisplayName = "Halfway through a rest, halfway across the space")]
    [DataRow(6.0, 100.0, DisplayName = "At an instant syllable, at its end")]
    [DataRow(9.0, 100.0, DisplayName = "After the last syllable, at its end")]
    public void TheLensIsAsFarThroughTheTextAsThroughTheSinging(double seconds, double expected) =>
        Assert.AreEqual(expected, LyricLens.PlaceOf(Lyrics, Lefts, Rights, seconds), 1e-9);

    [TestMethod]
    [DataRow(0.5, -1)]
    [DataRow(1.0, 0)]
    [DataRow(1.99, 0)]
    [DataRow(2.5, -1)]
    [DataRow(3.0, 1)]
    [DataRow(6.0, 2)]
    [DataRow(6.5, -1)]
    public void TheSyllableBeingSungIsTheOneWhoseNoteTheTimeIsIn(double seconds, int expected) =>
        Assert.AreEqual(expected, LyricLens.SungAt(Lyrics, seconds));
}
