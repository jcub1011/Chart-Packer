using ChartShared.Core.Charting.Lint.Rules;

namespace ChartShared.Core.Charting.Lint;

/// <summary>The chart lint rules there are (charter-spec §6), which <see cref="ChartLinter"/> runs by default.</summary>
/// <remarks>
/// Rules are listed here rather than found by reflection, which Native AOT can't do safely, each with its ID in
/// <see cref="Validation.ChrRules"/>. CM2 added the timing, 5-fret and song rules, CM3 the drums and phrase rules, and
/// vocal editing the vocal rules.
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
        new PhraseEmptyOnDifficultyRule(),
        new NoStarPowerRule(),
        new EmptyPartRule(),
        new LowerDifficultyDenserRule(),
        new NoSectionsRule(),
        new UnratedPartRule(),
        new SongLengthRule(),
        new EndEventRule(),
        new MissingDifficultyRule(),
        new VocalNotesOverlapRule(),
        new VocalNoteWithoutLyricRule(),
        new VocalNoteOutsidePhraseRule(),
        new LyricWithoutNoteRule(),
        new SlideWithoutNoteRule(),
        new HarmonyWithoutFirstRule(),
    ];

    /// <summary>
    /// The rules Chart Builder runs: all but CHR085, as the editor places <c>[end]</c> itself (<see cref="SongEventPlacement"/>)
    /// and its model never holds it.
    /// </summary>
    public static IReadOnlyList<IChartLintRule> ForEditor { get; } = [.. All.Where(static rule => rule is not EndEventRule)];
}
