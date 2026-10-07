using ChartPacker.Core.Charting.Lint.Rules;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>The chart lint rules there are (charter-spec §6), which <see cref="ChartLinter"/> runs by default.</summary>
/// <remarks>
/// Rules are listed here rather than found by reflection, which Native AOT can't do safely, each with its ID in
/// <see cref="Validation.ChrRules"/>. CM2 added the timing, 5-fret and song rules, and CM3 the drums and phrase rules.
/// </remarks>
public static class ChartLintRules
{
    /// <summary>Every rule, in the order they're run.</summary>
    public static IReadOnlyList<IChartLintRule> All { get; } =
    [
        new NoTimeSignatureAtStartRule(),
        new TempoOutOfRangeRule(),
        new AfterTheAudioRule(),
        new OffGridRule(),
        new DuplicateMarkersRule(),
        new SustainIntoNextNoteRule(),
        new SustainUnderCutoffRule(),
        new OpenInChordRule(),
        new RedundantForceRule(),
        new ForcedRepeatRule(),
        new FirstNoteHopoRule(),
        new CloseNotesRule(),
        new TomMarkerCoversNothingRule(),
        new TooManyPadsRule(),
        new FillInStarPowerRule(),
        new FillEndsWithoutNoteRule(),
        new RollTooFewRule(),
        new TomMarkersOnFourLaneRule(),
        new FifthPadNotFiveLaneRule(),
        new Drums2xTrackRule(),
        new EmptyStarPowerRule(),
        new EmptySoloRule(),
        new OverlappingPhrasesRule(),
        new NotesInBigRockEndingRule(),
        new MissingDifficultyRule(),
        new LowerDifficultyDenserRule(),
        new NoSectionsRule(),
        new UnratedPartRule(),
        new SongLengthRule(),
        new EndEventRule(),
    ];
}
