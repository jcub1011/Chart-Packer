using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Library;

/// <summary>How many notes a part has on one of its difficulties, or one of its harmony lines.</summary>
/// <param name="Difficulty">The difficulty; <see langword="null"/> for a part without difficulties, such as vocals.</param>
/// <param name="Line">A harmony's line, 1 to 3; 0 for any other part.</param>
/// <param name="Count">Its notes, a chord counting once.</param>
public readonly record struct NoteCount(ChartDifficulty? Difficulty, int Line, int Count);

/// <summary>A part a chart has.</summary>
/// <param name="Difficulties">The difficulties it has notes for.</param>
/// <param name="NoteCounts">
/// Its notes on each difficulty or line that has any, by difficulty from easy, then by line;
/// none for a part whose notes aren't counted, such as dance.
/// </param>
public sealed record ChartPart(LibraryDifficulties Difficulties, IReadOnlyList<NoteCount> NoteCounts);
