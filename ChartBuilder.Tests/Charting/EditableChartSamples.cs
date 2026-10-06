using ChartPacker.Core.Charting;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

/// <summary>An editable chart with something in every list the model has, and a change to make to each.</summary>
internal static class EditableChartSamples
{
    public static readonly byte[] VocalsChunk = [.. "MTrk"u8, 0, 0, 0, 4, 0, 0xFF, 0x2F, 0];

    /// <summary>A change to a chart, and where the comparer should say it is.</summary>
    public sealed record Change(string Name, Action<EditableChart> Apply, ModelDifferenceKind Kind, ChartInstrument? Part = null, ChartDifficulty? Difficulty = null, long? Tick = null);

    public static EditableChart Full()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Drums, ChartInstrument.GhlGuitar }, DrumsVariant.Pro));
        chart.TempoMap.Tempos.Add(new TempoChange(1920, 400_000));
        chart.TempoMap.TimeSignatures.Add(new TimeSignatureMarker(3840, 3, 4));
        chart.TempoMap.Anchors.Add(new TempoAnchor(1920, 2_000_000));
        chart.TempoTrackRaw.Add(new RawEvent(0, [0xFF, 0x59, 2, 0, 0]));
        chart.Events.Add(TextEvent.Of(960, "[section Intro]"));
        chart.EventsRaw.Add(new RawEvent(480, [0x90, 24, 100]));

        var guitar = chart.PartOf(ChartInstrument.Guitar)!;
        guitar.EnhancedOpens = true;
        guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(0, 1, 0));
        guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(480, 240, 2));
        guitar[ChartDifficulty.Expert].Notes.Add(new EditableNote(480, 1, ChartNote.OpenLane));
        guitar[ChartDifficulty.Expert].SetFlags(480, ChordFlags.ForceHopo);
        guitar[ChartDifficulty.Hard].Notes.Add(new EditableNote(0, 0, 1));
        guitar.Phrases.Add(new Phrase(PhraseKind.StarPower, 0, 960));
        guitar.Phrases.Add(new Phrase(PhraseKind.Solo, 960, 480));
        guitar.TextEvents.Add(TextEvent.Of(0, "[idle]"));
        guitar.RawEvents.Add(new RawEvent(0, [0x90, 40, 100]));

        var drums = chart.PartOf(ChartInstrument.Drums)!;
        drums.ChartDynamics = true;
        var expert = drums[ChartDifficulty.Expert];
        expert.Notes.Add(new EditableNote(0, 1, 0));
        expert.Notes.Add(new EditableNote(0, 1, 0, DoubleKick: true));
        expert.Notes.Add(new EditableNote(0, 1, 2, NoteDynamics.Accent));
        expert.Notes.Add(new EditableNote(480, 1, 3, NoteDynamics.Ghost));
        expert.Mixes.Add(new DrumMix(0, 0, DrumMix.DiscoFlipSetting));
        drums.TomMarkers.Add(new TomMarker(3, 480, 10));
        drums.Phrases.Add(new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Hard));
        drums.Phrases.Add(new Phrase(PhraseKind.DrumFill, 1920, 480));

        chart.PartOf(ChartInstrument.GhlGuitar)![ChartDifficulty.Easy].Notes.Add(new EditableNote(0, 1, 5));
        chart.AddPassthrough(new PassthroughTrack(VocalsChunk, "PART VOCALS"));
        return chart;
    }

    /// <summary>A change to each thing <see cref="Full"/> holds, each made to its own copy.</summary>
    public static IReadOnlyList<Change> Changes { get; } =
    [
        new("Name", c => c.Name = "Other", ModelDifferenceKind.Name),
        new("TempoAdded", c => c.TempoMap.Tempos.Add(new TempoChange(2880, 300_000)), ModelDifferenceKind.Tempo, Tick: 2880),
        new("TempoChanged", c => Swap(c.TempoMap.Tempos, new TempoChange(1920, 400_000), new TempoChange(1920, 410_000)), ModelDifferenceKind.Tempo, Tick: 1920),
        new("TimeSignatureRemoved", c => c.TempoMap.TimeSignatures.Remove(new TimeSignatureMarker(3840, 3, 4)), ModelDifferenceKind.TimeSignature, Tick: 3840),
        new("TimeSignatureClocksChanged", c => Swap(c.TempoMap.TimeSignatures, new TimeSignatureMarker(3840, 3, 4), new TimeSignatureMarker(3840, 3, 4, 12)), ModelDifferenceKind.TimeSignature, Tick: 3840),
        new("AnchorMoved", c => Swap(c.TempoMap.Anchors, new TempoAnchor(1920, 2_000_000), new TempoAnchor(1920, 2_000_001)), ModelDifferenceKind.Anchor, Tick: 1920),
        new("TempoTrackEventAdded", c => c.TempoTrackRaw.Add(new RawEvent(100, [0xFF, 0x06, 1, (byte)'x'])), ModelDifferenceKind.TempoTrackEvent, Tick: 100),
        new("SectionAdded", c => c.Events.Add(TextEvent.Of(1920, "[section Verse]")), ModelDifferenceKind.GlobalEvent, Tick: 1920),
        new("GlobalRawRemoved", c => c.EventsRaw.Remove(new RawEvent(480, [0x90, 24, 100])), ModelDifferenceKind.GlobalEvent, Tick: 480),
        new("DrumsVariantChanged", c => c.DrumsVariant = DrumsVariant.FiveLane, ModelDifferenceKind.DrumsVariant),
        new("PartRemoved", c => c.RemovePart(ChartInstrument.GhlGuitar), ModelDifferenceKind.Part, ChartInstrument.GhlGuitar),
        new("PartAdded", c => c.AddPart(ChartInstrument.Bass), ModelDifferenceKind.Part, ChartInstrument.Bass),
        new("EnhancedOpensOff", c => c.PartOf(ChartInstrument.Guitar)!.EnhancedOpens = false, ModelDifferenceKind.PartSetting, ChartInstrument.Guitar),
        new("ChartDynamicsOff", c => c.PartOf(ChartInstrument.Drums)!.ChartDynamics = false, ModelDifferenceKind.PartSetting, ChartInstrument.Drums),
        new("SustainChanged", c => Swap(c.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes, new EditableNote(480, 240, 2), new EditableNote(480, 120, 2)),
            ModelDifferenceKind.Note, ChartInstrument.Guitar, ChartDifficulty.Expert, 480),
        new("NoteAdded", c => c.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Hard].Notes.Add(new EditableNote(960, 1, 1)),
            ModelDifferenceKind.Note, ChartInstrument.Guitar, ChartDifficulty.Hard, 960),
        new("AccentBecameGhost", c => Swap(c.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes, new EditableNote(0, 1, 2, NoteDynamics.Accent), new EditableNote(0, 1, 2, NoteDynamics.Ghost)),
            ModelDifferenceKind.Note, ChartInstrument.Drums, ChartDifficulty.Expert, 0),
        new("DoubleKickRemoved", c => c.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes.Remove(new EditableNote(0, 1, 0, DoubleKick: true)),
            ModelDifferenceKind.Note, ChartInstrument.Drums, ChartDifficulty.Expert, 0),
        new("ChordFlagsChanged", c => c.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].SetFlags(480, ChordFlags.Tap),
            ModelDifferenceKind.ChordFlags, ChartInstrument.Guitar, ChartDifficulty.Expert, 480),
        new("MixChanged", c => Swap(c.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Mixes, new DrumMix(0, 0, "d"), new DrumMix(0, 0, "")),
            ModelDifferenceKind.Mix, ChartInstrument.Drums, ChartDifficulty.Expert, 0),
        new("SoloRemoved", c => c.PartOf(ChartInstrument.Guitar)!.Phrases.Remove(new Phrase(PhraseKind.Solo, 960, 480)), ModelDifferenceKind.Phrase, ChartInstrument.Guitar, Tick: 960),
        new("RollExtended", c => Swap(c.PartOf(ChartInstrument.Drums)!.Phrases, new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Hard), new Phrase(PhraseKind.Roll, 960, 480, ChartDifficulty.Medium)),
            ModelDifferenceKind.Phrase, ChartInstrument.Drums, Tick: 960),
        new("TomMarkerAdded", c => c.PartOf(ChartInstrument.Drums)!.TomMarkers.Add(new TomMarker(4, 960, 5)), ModelDifferenceKind.TomMarker, ChartInstrument.Drums, Tick: 960),
        new("PartTextAdded", c => c.PartOf(ChartInstrument.Guitar)!.TextEvents.Add(TextEvent.Of(240, "[play]")), ModelDifferenceKind.PartEvent, ChartInstrument.Guitar, Tick: 240),
        new("PartRawRemoved", c => c.PartOf(ChartInstrument.Guitar)!.RawEvents.Remove(new RawEvent(0, [0x90, 40, 100])), ModelDifferenceKind.PartEvent, ChartInstrument.Guitar, Tick: 0),
        new("PassthroughChanged", c => c.SetPassthrough(0, new PassthroughTrack([.. VocalsChunk[..^1], 1], "PART VOCALS")), ModelDifferenceKind.Passthrough),
        new("PassthroughAdded", c => c.AddPassthrough(new PassthroughTrack(VocalsChunk, "HARM1")), ModelDifferenceKind.Passthrough),
    ];

    public static Change ChangeNamed(string name) => Changes.Single(c => c.Name == name);

    private static void Swap<T>(TickList<T> list, T from, T to)
    {
        Assert.IsTrue(list.Remove(from), $"{from} isn't in the sample.");
        Assert.IsTrue(list.Add(to));
    }
}
