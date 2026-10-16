using System.Text;
using ChartShared.Core.Charts;
using ChartShared.Core.Merging;
using ChartShared.Core.Tests.Charts;

namespace ChartShared.Core.Tests.Merging;

[TestClass]
public sealed class LyricsExportTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoMetadata = [];

    [TestMethod]
    public void MidiExportHasTheTempoAndVocalTracksOnly()
    {
        byte[] midi = new MidiBuilder()
            .Track("tempo").Tempo(0, 120).Tempo(1920, 90).TimeSignature(0, 3, 2)
            .Track("PART GUITAR").Note(0, 96)
            .Track("HARM1").Note(0, 62).Meta(0, 0x05, Encoding.Latin1.GetBytes("hey"))
            .Track("PART VOCALS")
            .Note(0, 105, 960)
            .Note(0, 60, 240).Meta(0, 0x05, Encoding.Latin1.GetBytes("Hel-"))
            .Note(480, 64, 240).Meta(480, 0x05, Encoding.Latin1.GetBytes("lo"))
            .Text(720, "[idle]")
            .Build();

        byte[] exported = LyricsExport.ToMidi(midi, "notes.mid", NoMetadata);

        Assert.AreSequenceEqual([null, "PART VOCALS", "HARM1"], [.. TrackNames(exported)]);
        var before = VocalReader.Read(midi, "notes.mid");
        var after = VocalReader.Read(exported, "notes.mid");
        Assert.IsTrue(after.Tempo.SameAs(before.Tempo));
        Assert.HasCount(2, after.Parts);
        for (int i = 0; i < before.Parts.Count; i++)
        {
            Assert.AreSequenceEqual([.. before.Parts[i].Notes], [.. after.Parts[i].Notes]);
            Assert.AreSequenceEqual([.. before.Parts[i].Lyrics], [.. after.Parts[i].Lyrics]);
            Assert.AreSequenceEqual([.. before.Parts[i].Phrases], [.. after.Parts[i].Phrases]);
        }

        // Events the vocals model doesn't read come along too.
        Assert.Contains("[idle]", Encoding.Latin1.GetString(exported));
    }

    [TestMethod]
    public void DelayAndOffsetAreWorkedIntoTheTicks()
    {
        byte[] midi = new MidiBuilder()
            .Track("tempo").Tempo(0, 120)
            .Track("PART VOCALS").Note(0, 105, 480).Note(0, 60, 240).Meta(0, 0x05, Encoding.Latin1.GetBytes("one"))
            .Build();
        IReadOnlyList<KeyValuePair<string, string>> metadata = [KeyValuePair.Create("delay", "250")];

        // 250 ms of delay and 250 ms of offset: half a second, a beat at 120 BPM.
        byte[] exported = LyricsExport.ToMidi(midi, "notes.mid", metadata, TimeSpan.FromMilliseconds(250));

        var part = VocalReader.Read(exported, "notes.mid").Main!;
        Assert.AreSequenceEqual([new VocalLyric(480, "one")], [.. part.Lyrics]);
        Assert.AreSequenceEqual([new VocalNote(480, 240, 60)], [.. part.Notes]);
        Assert.AreSequenceEqual([new VocalPhrase(480, 480)], [.. part.Phrases]);
    }

    [TestMethod]
    public void VocalsMovedBeforeTheStartAreRefused()
    {
        byte[] midi = new MidiBuilder()
            .Track("PART VOCALS").Note(0, 60).Meta(0, 0x05, Encoding.Latin1.GetBytes("one"))
            .Build();

        Assert.ThrowsExactly<ChartFormatException>(() => LyricsExport.ToMidi(midi, "notes.mid", NoMetadata, TimeSpan.FromMilliseconds(-10)));
    }

    [TestMethod]
    public void AChartWithoutVocalsIsRefused()
    {
        byte[] midi = new MidiBuilder().Track("tempo").Tempo(0, 120).Track("PART GUITAR").Note(0, 96).Build();

        Assert.ThrowsExactly<ChartFormatException>(() => LyricsExport.ToMidi(midi, "notes.mid", NoMetadata));
    }

    [TestMethod]
    public void DotChartLyricsAndPhrasesAreExported()
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
            }
            """);

        byte[] exported = LyricsExport.ToMidi(chart, "notes.chart", NoMetadata);

        var vocals = VocalReader.Read(exported, "notes.mid");
        Assert.AreEqual(192, vocals.Tempo.Resolution);
        Assert.AreSequenceEqual([new VocalLyric(192, "Hel-"), new VocalLyric(288, "lo")], [.. vocals.Main!.Lyrics]);
        Assert.AreSequenceEqual([new VocalPhrase(192, 192)], [.. vocals.Main.Phrases]);
        Assert.AreSequenceEqual([null, "PART VOCALS"], [.. TrackNames(exported)]);
    }

    private static List<string?> TrackNames(byte[] midi)
    {
        var (_, _, chunks) = MidiFile.ReadChunks(midi);
        return [.. chunks.Where(c => c.Id == "MTrk").Select(c => MidiFile.NameOf(midi, MidiFile.ReadEvents(midi, c)))];
    }
}
