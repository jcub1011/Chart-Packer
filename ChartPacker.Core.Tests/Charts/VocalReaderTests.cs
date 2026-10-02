using System.Text;
using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Tests.Charts;

[TestClass]
public sealed class VocalReaderTests
{
    [TestMethod]
    public void MidiVocalsHaveNotesLyricsAndPhrases()
    {
        byte[] midi = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS")
            .Note(0, 105, 960)
            .Note(0, 60, 240).Meta(0, 0x05, Encoding.Latin1.GetBytes("Hel-"))
            .Note(480, 64, 240).Meta(480, 0x05, Encoding.Latin1.GetBytes("lo"))
            .Note(720, 96, 60)
            .Text(720, "[idle]")
            .Build();

        var part = VocalReader.Read(midi, "notes.mid").Main!;

        Assert.AreEqual("PART VOCALS", part.TrackName);
        Assert.AreSequenceEqual([new VocalNote(0, 240, 60), new VocalNote(480, 240, 64)], [.. part.Notes]);
        Assert.AreSequenceEqual([new VocalLyric(0, "Hel-"), new VocalLyric(480, "lo")], [.. part.Lyrics]);
        Assert.AreSequenceEqual([new VocalPhrase(0, 960)], [.. part.Phrases]);
    }

    [TestMethod]
    public void MidiTextEventsAreLyricsUnlessBracketed()
    {
        byte[] midi = new MidiBuilder()
            .Track("PART VOCALS").Text(0, "[play]").Text(0, "Hey").Note(0, 50)
            .Build();

        var part = VocalReader.Read(midi, "notes.mid").Main!;

        Assert.AreSequenceEqual([new VocalLyric(0, "Hey")], [.. part.Lyrics]);
    }

    [TestMethod]
    public void HarmoniesAreReadAndVocalsComeFirst()
    {
        byte[] midi = new MidiBuilder()
            .Track("HARM2").Note(0, 62)
            .Track("PART GUITAR").Note(0, 96)
            .Track("HARM1").Note(0, 60)
            .Track("PART VOCALS").Note(0, 64)
            .Build();

        var vocals = VocalReader.Read(midi, "notes.mid");

        Assert.AreSequenceEqual(["PART VOCALS", "HARM1", "HARM2"], [.. vocals.Parts.Select(p => p.TrackName)]);
        Assert.AreEqual(64, vocals.Main!.Notes.Single().Pitch);
    }

    [TestMethod]
    public void HarmonyIsShownWithoutVocals()
    {
        byte[] midi = new MidiBuilder().Track("HARM1").Note(0, 60).Build();

        Assert.AreEqual("HARM1", VocalReader.Read(midi, "notes.mid").Main!.TrackName);
    }

    [TestMethod]
    public void AnEmptyVocalsTrackGivesWayToAHarmonyWithLyrics()
    {
        byte[] midi = new MidiBuilder()
            .Track("PART VOCALS")
            .Track("HARM1").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build();

        Assert.AreEqual("HARM1", VocalReader.Read(midi, "notes.mid").Main!.TrackName);
    }

    [TestMethod]
    public void VocalsWithLyricsAreShownBeforeHarmonies()
    {
        byte[] midi = new MidiBuilder()
            .Track("HARM1").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("oh"))
            .Track("PART VOCALS").Note(0, 64).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Build();

        Assert.AreEqual("PART VOCALS", VocalReader.Read(midi, "notes.mid").Main!.TrackName);
    }

    [TestMethod]
    public void FirstTickIsTheEarliestEventOfAnyPart()
    {
        byte[] midi = new MidiBuilder()
            .Track("PART VOCALS").Note(480, 60).Meta(600, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Track("HARM1").Note(240, 105, 480)
            .Build();

        Assert.AreEqual(240L, VocalReader.Read(midi, "notes.mid").FirstTick);
        Assert.IsNull(VocalReader.Read(new MidiBuilder().Track("PART VOCALS").Build(), "notes.mid").FirstTick);
    }

    [TestMethod]
    public void MidiLyricsAreReadAsUtf8WhenTheyAreAndLatin1Otherwise()
    {
        byte[] midi = new MidiBuilder()
            .Track("PART VOCALS")
            .Note(0, 60).Meta(0, 0x05, [0xC3, 0xA9])
            .Note(480, 60).Meta(480, 0x05, [0xE9])
            .Build();

        Assert.AreSequenceEqual(["é", "é"], [.. VocalReader.Read(midi, "notes.mid").Main!.Lyrics.Select(l => l.Text)]);
    }

    [TestMethod]
    public void MidiWithoutVocalsHasNone()
    {
        byte[] midi = new MidiBuilder().Track("PART GUITAR").Note(0, 96).Track("PART VOCALS").Build();

        var vocals = VocalReader.Read(midi, "notes.mid");

        Assert.IsFalse(vocals.HasVocals);
    }

    [TestMethod]
    public void ChartLyricsAreReadFromEvents()
    {
        byte[] chart = Encoding.UTF8.GetBytes("""
            [Song]
            {
              Resolution = 192
            }
            [SyncTrack]
            {
              0 = B 120000
            }
            [Events]
            {
              0 = E "section Intro"
              192 = E "phrase_start"
              192 = E "lyric Hel-"
              288 = E "lyric lo"
              384 = E "phrase_end"
              400 = E "phrase_start"
              400 = E "lyric bye"
            }
            """);

        var vocals = VocalReader.Read(chart, "notes.chart");
        var part = vocals.Main!;

        Assert.AreEqual(192, vocals.Tempo.Resolution);
        Assert.IsEmpty(part.Notes);
        Assert.AreSequenceEqual([new VocalLyric(192, "Hel-"), new VocalLyric(288, "lo"), new VocalLyric(400, "bye")], [.. part.Lyrics]);
        // An open phrase ends a beat after the last event.
        Assert.AreSequenceEqual([new VocalPhrase(192, 192), new VocalPhrase(400, 192)], [.. part.Phrases]);
    }

    [TestMethod]
    public void ChartWithoutLyricsHasNoVocals()
    {
        Assert.IsEmpty(VocalReader.Read(Encoding.UTF8.GetBytes(ChartReaderTests.Chart), "notes.chart").Parts);
    }

    [TestMethod]
    [DataRow("Hel-", "Hel", true, false)]
    [DataRow("well=", "well-", true, false)]
    [DataRow("hey#", "hey", false, true)]
    [DataRow("yeah^", "yeah", false, true)]
    [DataRow("oh-#", "oh", true, true)]
    [DataRow("word$", "word", false, false)]
    [DataRow("a_b", "a b", false, false)]
    [DataRow("stay*", "stay", false, false)]
    [DataRow("<b>Hel-", "Hel", true, false)]
    [DataRow("lo</b>", "lo", false, false)]
    [DataRow("Hel-</b>", "Hel", true, false)]
    [DataRow("<color=#FF0000>hey</color>", "hey", false, false)]
    [DataRow("<i>shout#</i>", "shout", false, true)]
    [DataRow("a<br>b", "a b", false, false)]
    [DataRow("<3", "<3", false, false)]
    public void SyllablesLoseTheirSymbols(string lyric, string text, bool joins, bool unpitched)
    {
        var syllable = LyricSyllable.Of(lyric);

        Assert.AreEqual(text, syllable.Text);
        Assert.AreEqual(joins, syllable.JoinsNext);
        Assert.AreEqual(unpitched, syllable.IsUnpitched);
        Assert.IsFalse(syllable.IsSlide);
    }

    [TestMethod]
    public void PlusIsASlide()
    {
        var syllable = LyricSyllable.Of("+");

        Assert.IsTrue(syllable.IsSlide);
        Assert.AreEqual("", syllable.Text);
    }
}
