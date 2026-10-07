using ChartPacker.Core.Charting.Lint.Rules;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>The chart lint rules there are (charter-spec §6), which <see cref="ChartLinter"/> runs by default.</summary>
/// <remarks>
/// Rules are listed here rather than found by reflection, which Native AOT can't do safely. CM2 added the timing, 5-fret
/// and song rules; CM3 adds the drums and phrase rules, each with its ID in <see cref="Validation.ChrRules"/>.
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
        new MissingDifficultyRule(),
        new LowerDifficultyDenserRule(),
        new NoSectionsRule(),
        new UnratedPartRule(),
        new SongLengthRule(),
        new EndEventRule(),
    ];
}
