using System.Text.Json.Serialization;
using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>
/// One prepared training example (spec §6.1 Prepare): derived figures only — beat patterns and
/// features with the reference's kept patterns — never the songs' notes, audio or text (§6.3).
/// </summary>
/// <param name="SourceRank">The source difficulty's rank by the difficulty model (<see cref="NeuralConditioning.SourceRank"/>); null when it can't be rated.</param>
/// <param name="Offsets">Each beat's source onsets, in 48ths of the beat.</param>
/// <param name="Scalars">Each beat's raw scalars, <see cref="ScalarsPerBeat"/> a beat (<see cref="PreparedParts.ScalarsAt"/>).</param>
/// <param name="SecondsPerBeat">Each beat's length in seconds.</param>
/// <param name="RiffGroups">Each beat's riff (<see cref="BeatFrame.RiffKey"/>) as a number, the same for the same key.</param>
/// <param name="StarPowerPhrases">Each beat's star power phrase (<see cref="BeatFrame.StarPowerPhrase"/>), -1 for none.</param>
/// <param name="SoloPhrases">Each beat's solo (<see cref="BeatFrame.SoloPhrase"/>), -1 for none.</param>
/// <param name="Labels">The official target's kept pattern in each beat.</param>
/// <param name="RulesLabels">Rules' draft's kept pattern in each beat, labelled the same way, for the evaluation baseline.</param>
public sealed record PreparedPart(
    string SongKey,
    string Style,
    double? SourceRank,
    string Instrument,
    string Pair,
    int[] SourcePatterns,
    int[][] Offsets,
    sbyte[] Scalars,
    float[] SecondsPerBeat,
    int[] RiffGroups,
    int[] StarPowerPhrases,
    int[] SoloPhrases,
    int[] Labels,
    int[] RulesLabels)
{
    /// <summary>How many raw scalars each beat keeps.</summary>
    public const int ScalarsPerBeat = 9;
}

/// <summary>A prepared training set: examples plus the vocabularies to build a pack from.</summary>
public sealed record TrainingSet(
    int BeatSchemaVersion,
    string[] Styles,
    string[] Pairs,
    string[] Instruments,
    List<PreparedPart> Examples,
    string TrainerVersion);

/// <summary>Reading a prepared part's beats back.</summary>
internal static class PreparedParts
{
    /// <summary>Packs a beat's raw scalars into <paramref name="dest"/>, <see cref="PreparedPart.ScalarsPerBeat"/> long.</summary>
    public static void Pack(BeatFrame beat, Span<sbyte> dest)
    {
        dest[0] = (sbyte)Math.Clamp(beat.BeatInMeasure, 0, 100);
        dest[1] = (sbyte)beat.GapBucket;
        dest[2] = (sbyte)(beat.PhraseStart ? 1 : 0);
        dest[3] = (sbyte)(beat.SectionStart ? 1 : 0);
        dest[4] = (sbyte)(beat.HasSustain ? 1 : 0);
        dest[5] = (sbyte)Math.Clamp(beat.RiffRepeat, 0, 100);
        dest[6] = (sbyte)Math.Clamp(beat.SourceTicks.Length, 0, 100);
        dest[7] = (sbyte)(beat.HasChord ? 1 : 0);
        dest[8] = (sbyte)(beat.MelodyTurn ? 1 : 0);
    }

    /// <summary>Beat <paramref name="i"/>'s scalars, as <see cref="BeatFrame.Scalars"/> gave them.</summary>
    public static BeatScalars ScalarsAt(this PreparedPart part, int i)
    {
        var s = part.Scalars.AsSpan(i * PreparedPart.ScalarsPerBeat, PreparedPart.ScalarsPerBeat);
        return new BeatScalars(
            s[0], s[1], s[2] != 0, s[3] != 0, s[4] != 0,
            part.StarPowerPhrases[i] >= 0, part.SoloPhrases[i] >= 0,
            s[5], s[6], s[7] != 0, s[8] != 0, part.SecondsPerBeat[i]);
    }

    /// <summary>Each beat's scalars.</summary>
    public static BeatScalars[] AllScalars(this PreparedPart part) =>
        [.. Enumerable.Range(0, part.SourcePatterns.Length).Select(part.ScalarsAt)];

    /// <summary>Each beat's candidates (§3.4 step 2).</summary>
    public static int[][] Candidates(this PreparedPart part) =>
        [.. part.Offsets.Select(static o => BeatPattern.CandidatesFor(o).ToArray())];

    /// <summary>
    /// The part's beats as the decoder reads them: a 4/4 grid at <paramref name="resolution"/>, each onset at its 48th, with
    /// the riffs and phrases the part had.
    /// </summary>
    public static List<BeatFrame> Frames(this PreparedPart part, int resolution)
    {
        var frames = new List<BeatFrame>(part.SourcePatterns.Length);
        for (int i = 0; i < part.SourcePatterns.Length; i++)
        {
            long start = (long)i * resolution;
            var s = part.ScalarsAt(i);
            var offsets = part.Offsets[i];
            frames.Add(new BeatFrame(
                start, i / 4, s.BeatInMeasure, part.SourcePatterns[i], offsets,
                [.. offsets.Select(o => start + ((long)o * resolution / BeatPattern.UnitsPerBeat))],
                s.GapBucket, s.PhraseStart, s.SectionStart, s.HasSustain,
                part.StarPowerPhrases[i], part.SoloPhrases[i], "g" + part.RiffGroups[i], s.RiffRepeat,
                s.SecondsPerBeat, s.HasChord, s.MelodyTurn));
        }

        return frames;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TrainingSet))]
[JsonSerializable(typeof(PreparedPart))]
internal partial class TrainingSetJsonContext : JsonSerializerContext
{
}
