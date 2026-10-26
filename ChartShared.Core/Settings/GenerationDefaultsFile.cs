using System.Text.Json.Serialization;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;

namespace ChartShared.Core.Settings;

/// <summary>
/// The generator lower difficulties are drafted by and its options, which every run starts from (improved-difficulty-
/// generator-spec §4.4): Chart Builder's Reduce, and Chart Packer's Validator and Generate Lower Difficulties.
/// </summary>
public sealed class GenerationDefaults
{
    /// <summary>The Rules method's id: the default reduction.</summary>
    public const string RulesMethodId = "rules";

    /// <summary>How far the density slider's ends move each chord's score (§3.4): trained emissions have multi-unit
    /// magnitudes, so the slider spans several units.</summary>
    public const double DensityScale = 6.0;

    /// <summary>The method: <see cref="RulesMethodId"/> or <see cref="NeuralDifficultyGenerator.NeuralMethodId"/>.</summary>
    public string MethodId { get; set; } = RulesMethodId;

    /// <summary>The neural pack, by model name; null for the first installed.</summary>
    public string? Pack { get; set; }

    /// <summary>The style the draft imitates (§3.3); Any by default.</summary>
    public string Style { get; set; } = "Any";

    /// <summary>Fewer notes (−1) to more notes (+1); 0 changes nothing (§3.4).</summary>
    public double Density { get; set; }

    /// <summary>Reduce all chained (each from the one above) or each from Expert (§3.6).</summary>
    public bool Chained { get; set; } = true;

    /// <summary>Fit kept notes into the target's usual lane range; off keeps every lane the source uses.</summary>
    public bool CapLanes { get; set; } = true;

    /// <summary>Whether the method is Neural.</summary>
    [JsonIgnore]
    public bool IsNeural => string.Equals(MethodId, NeuralDifficultyGenerator.NeuralMethodId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The defaults with every value kept to what it can be.</summary>
    public GenerationDefaults Normalized() => new()
    {
        MethodId = IsNeural ? NeuralDifficultyGenerator.NeuralMethodId : RulesMethodId,
        Pack = string.IsNullOrWhiteSpace(Pack) ? null : Pack,
        Style = string.IsNullOrWhiteSpace(Style) ? "Any" : Style,
        Density = double.IsFinite(Density) ? Math.Clamp(Density, -1, 1) : 0,
        Chained = Chained,
        CapLanes = CapLanes,
    };

    /// <summary>A run's options from these defaults.</summary>
    public GenerationOptions Options() => new(
        Style: Style,
        DensityBias: Density * DensityScale,
        Chained: Chained,
        ModelName: Pack,
        CapLanes: CapLanes);

    /// <summary>
    /// The generator these defaults pick, drafting from the packs in <paramref name="store"/> and voicing each part's
    /// targets by <paramref name="rules"/> for its family; null for Rules, or where there's no store.
    /// </summary>
    public IDifficultyGenerator? Generator(IGeneratorModelStore? store, ReductionDefaults rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return IsNeural && store is not null
            ? new NeuralDifficultyGenerator(store, (part, target) => rules.FretsFor(ChartInstruments.KindOf(part), target))
            : null;
    }
}

/// <summary>
/// <c>generation-defaults.json</c>: the <see cref="GenerationDefaults"/> both apps read, beside the default reduction rules
/// (<see cref="ReductionRulesFile"/>). Chart Builder's Reduce defaults write it; runs only read it.
/// </summary>
/// <remarks>
/// It's read afresh by each <see cref="Load"/>, and a save writes only what this process changed since it last loaded
/// (<see cref="JsonSettingsFile{T}"/>). The path is always passed in; tests pass temp files.
/// </remarks>
public sealed class GenerationDefaultsFile
{
    private readonly JsonSettingsFile<GenerationDefaults> _file;

    /// <param name="path">The file, <see cref="UserConfigPaths.GenerationDefaultsFile"/>.</param>
    public GenerationDefaultsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new JsonSettingsFile<GenerationDefaults>(path, GenerationDefaultsJsonContext.Default.GenerationDefaults);
    }

    /// <summary>The defaults as the file has them now; the shipped ones where it has none.</summary>
    public GenerationDefaults Load() => _file.Load().Normalized();

    /// <summary>Writes <paramref name="defaults"/>, keeping another process's changes to what this one didn't change.</summary>
    /// <returns>Whether the file was written.</returns>
    public bool Save(GenerationDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return _file.Save(defaults.Normalized());
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GenerationDefaults))]
internal sealed partial class GenerationDefaultsJsonContext : JsonSerializerContext
{
}
