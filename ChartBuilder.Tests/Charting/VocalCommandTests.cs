using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// The commands that edit a vocal part (charter-spec C3 item 7): each replaces the part's passthrough track with one
/// written again, and puts it back when it's undone.
/// </summary>
[TestClass]
public sealed class VocalCommandTests
{
    [TestMethod]
    public void ChangingAVocalPartAppliesRevertsAndRedoes()
    {
        var chart = WithVocals([new(0, 240, 60)], [new(0, "a")]);
        var content = Content(chart, VocalTrack.Vocals);

        var command = new ChangeVocals(VocalTrack.Vocals, content.With(notes: [new(0, 240, 62)]));
        AppliesRevertsAndRedoes(chart, command);

        Assert.AreEqual(62, Content(chart, VocalTrack.Vocals).Notes.Single().Pitch);
        Assert.AreEqual(SongChanges.Vocals, command.Affected.Song);
        Assert.IsEmpty(command.Affected.Regions);
        Assert.IsFalse(command.Affected.RebuildsAllTracks);
    }

    [TestMethod]
    public void AChangeWorkedOutFromAnotherVersionOfTheTrackIsRefused()
    {
        var chart = WithVocals([new(0, 240, 60)]);
        var stale = Content(chart, VocalTrack.Vocals).With(notes: [new(0, 240, 70)]);
        new ChangeVocals(VocalTrack.Vocals, Content(chart, VocalTrack.Vocals).With(notes: [new(0, 240, 62)])).Apply(chart);

        Refuses<InvalidOperationException>(chart, new ChangeVocals(VocalTrack.Vocals, stale));
        Refuses<InvalidOperationException>(chart, new ChangeVocals(VocalTrack.Harmony1, stale));
        Refuses<ArgumentException>(chart, new ChangeVocals(VocalTrack.Vocals, Content(chart, VocalTrack.Vocals)));
    }

    [TestMethod]
    public void AddingAVocalPartAppendsAnEmptyTrackOfItsName()
    {
        var chart = Guitar();
        int before = chart.Passthrough.Count;

        var after = AppliesRevertsAndRedoes(chart, new AddVocalPart(VocalTrack.Harmony2));

        Assert.HasCount(before + 1, after.Passthrough);
        Assert.AreEqual("HARM2", chart.Passthrough[^1].Name);
        Assert.IsEmpty(Content(chart, VocalTrack.Harmony2).Notes);
        Refuses<InvalidOperationException>(chart, new AddVocalPart(VocalTrack.Harmony2));
    }

    [TestMethod]
    public void APartTheChartHasUnderAnotherCaseIsntAddedAgain()
    {
        byte[] midi = new ChartPacker.Core.Tests.Charts.MidiBuilder(480).Track("Song").Tempo(0, 120).TimeSignature(0, 4, 2)
            .Track("harm1 ").Note(0, 60, 240)
            .Build();
        var chart = MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

        Refuses<InvalidOperationException>(chart, new AddVocalPart(VocalTrack.Harmony1));
    }

    [TestMethod]
    public void AnEditedPartIsReTickedWithTheChart()
    {
        var chart = WithVocals([new(240, 240, 60)], [new(240, "a")]);

        new ChangeResolution(960).Apply(chart);

        var content = Content(chart, VocalTrack.Vocals);
        Assert.AreEqual(new VocalNoteEvent(480, 480, 60), content.Notes.Single());
        Assert.AreEqual(480, content.Lyrics.Single().Tick);
    }

    [TestMethod]
    public void AnEditedAndAnAddedPartSaveAndReadBackAsEdited()
    {
        var chart = WithVocals([new(0, 240, 60), new(480, 240, 64)], [new(0, "Hel-"), new(480, "lo")], [new(0, 960)]);
        new AddVocalPart(VocalTrack.Harmony1).Apply(chart);
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        var vocals = VocalReader.Read(result.Bytes, "notes.mid");
        Assert.AreSequenceEqual(new[] { "PART VOCALS", "HARM1" }, [.. vocals.Parts.Select(p => p.TrackName)]);
        var main = vocals.Parts[0];
        Assert.AreSequenceEqual(new[] { new VocalNote(0, 240, 60), new VocalNote(480, 240, 64) }, [.. main.Notes]);
        Assert.AreSequenceEqual(new[] { new VocalLyric(0, "Hel-"), new VocalLyric(480, "lo") }, [.. main.Lyrics]);
        Assert.AreSequenceEqual(new[] { new VocalPhrase(0, 960) }, [.. main.Phrases]);
    }

    private static VocalTrackContent Content(EditableChart chart, VocalTrack track) =>
        VocalTrackContent.Of(chart.Passthrough[VocalTracks.IndexIn(chart, track)]);
}
