using ChartShared.Core.Generation.Neural;

namespace DifficultyTrainer;

/// <summary>Inspect (spec §6.1): prints a pack's manifest and validates it.</summary>
internal static class Inspect
{
    public static int Run(string[] args)
    {
        string packPath = args.FirstOrDefault(static a => !a.StartsWith('-')) ?? Args(args, "--pack", "");
        if (string.IsNullOrEmpty(packPath) || !File.Exists(packPath))
        {
            Console.Error.WriteLine("Usage: inspect <pack.neuralpack>");
            return 2;
        }

        try
        {
            var pack = NeuralPack.LoadFromBytes(File.ReadAllBytes(packPath), Path.GetFileName(packPath));
            var m = pack.Manifest;
            Console.WriteLine($"Name:        {m.Name}");
            Console.WriteLine($"Author:      {m.Author}");
            Console.WriteLine($"Format:      {m.FormatVersion}  Architecture: {m.Architecture}  Beat schema: {m.BeatSchemaVersion}");
            Console.WriteLine($"Description: {m.Description}");
            Console.WriteLine($"Styles:      {string.Join(", ", m.Styles.Select(static s => s.Label))}");
            Console.WriteLine($"Instruments: {string.Join(", ", m.Instruments)}");
            Console.WriteLine($"Pairs:       {string.Join(", ", m.Pairs)}");
            Console.WriteLine($"Inputs:      hidden={m.Inputs.HiddenSize} embed={m.Inputs.EmbeddingSize} rank={m.Inputs.UseRank} style={m.Inputs.UseStyle}");
            Console.WriteLine($"Vocabulary:  {string.Join(" ", m.Vocabulary)}");
            Console.WriteLine($"Gaps (48ths/beat): {string.Join(", ", m.MinGapTicks.Select(static kv => kv.Key + "=" + kv.Value))}");
            Console.WriteLine($"Training:    {m.Training.Songs} songs, {m.Training.Parts} parts, {m.Training.Studios}, {m.Training.Date}, {m.Training.TrainerVersion}");
            Console.WriteLine($"Evaluation:  {m.Training.Evaluation}");
            Console.WriteLine($"License:     {m.License}");
            Console.WriteLine($"Parameters:  {pack.Weights.ParameterCount}");
            Console.WriteLine("Status:      valid.");
            return 0;
        }
        catch (PackValidationException ex)
        {
            Console.Error.WriteLine($"Invalid pack: {ex.Message}");
            return 1;
        }
    }

    private static string Args(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return fallback;
    }
}
