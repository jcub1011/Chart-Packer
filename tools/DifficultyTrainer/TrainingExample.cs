using System.Text.Json.Serialization;

namespace DifficultyTrainer;

/// <summary>
/// One prepared training example (spec §6.1 Prepare): derived figures only — beat patterns and
/// features with the reference's kept patterns — never the songs' notes, audio or text (§6.3).
/// </summary>
public sealed record PreparedPart(
    string SongKey,
    string Style,
    int? Tier,
    string Instrument,
    string Pair,
    int[] SourcePatterns,
    int[][] Candidates,
    sbyte[] Scalars,
    int[] RiffGroups,
    int[] Labels);

/// <summary>A prepared training set: examples plus the vocabularies to build a pack from.</summary>
public sealed record TrainingSet(
    int BeatSchemaVersion,
    string[] Styles,
    string[] Targets,
    string[] Pairs,
    string[] Instruments,
    List<PreparedPart> Examples,
    string TrainerVersion);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TrainingSet))]
[JsonSerializable(typeof(PreparedPart))]
internal partial class TrainingSetJsonContext : JsonSerializerContext
{
}
