using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// A neural pack (§5): one file holding a trained BiGRU+CRF model with a manifest describing it.
/// Data only — nothing in it is ever executed. Loaded through <see cref="IGeneratorModelStore"/>
/// as <c>NeuralDifficulty</c> models.
/// </summary>
public sealed class NeuralPack
{
    /// <summary>Current pack format version (§5.4).</summary>
    public const int FormatVersion = 1;

    /// <summary>Previous readable format version (§5.4).</summary>
    public const int PreviousFormatVersion = 1;

    /// <summary>Largest pack accepted, in bytes (§5.3).</summary>
    public const long MaxPackBytes = 64L * 1024 * 1024;

    /// <summary>Largest parameter count accepted (§3.3 size requirement).</summary>
    public const int MaxParameters = 2_000_000;

    public required PackManifest Manifest { get; init; }
    public required PackWeights Weights { get; init; }

    /// <summary>Loads and fully validates a pack from the store (§5.3). Never half-used.</summary>
    /// <exception cref="PackValidationException">The pack is damaged or unsupported.</exception>
    public static async Task<NeuralPack> LoadAsync(
        IGeneratorModelStore store, GeneratorModelId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (id.Type != GeneratorType.NeuralDifficulty)
            throw new PackValidationException($"Model '{id.Name}' is not a neural pack.");

        byte[] bytes;
        try
        {
            bytes = await store.ReadAllBytesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new PackValidationException($"Neural pack '{id.Name}' is not installed.", ex);
        }

        return LoadFromBytes(bytes, id.Name);
    }

    /// <summary>Validates raw pack bytes and returns the pack.</summary>
    /// <exception cref="PackValidationException">Validation failed.</exception>
    public static NeuralPack LoadFromBytes(byte[] bytes, string name = "?")
    {
        if (bytes.Length > MaxPackBytes)
            throw new PackValidationException($"Neural pack '{name}' is {bytes.Length} bytes; the limit is {MaxPackBytes}.");

        NeuralPackFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, NeuralPackJsonContext.Default.NeuralPackFile);
        }
        catch (JsonException ex)
        {
            throw new PackValidationException($"Neural pack '{name}' is not readable.", ex);
        }

        if (file?.Manifest is null || file.Weights is null)
            throw new PackValidationException($"Neural pack '{name}' has no model in it.");

        file.Manifest.Validate(name);
        file.Weights.Validate(name, file.Manifest);
        VerifyChecksum(name, file);

        return new NeuralPack { Manifest = file.Manifest, Weights = file.Weights };
    }

    /// <summary>Serializes the pack for saving to the store.</summary>
    public byte[] ToBytes()
    {
        var file = new NeuralPackFile(Manifest, Weights);
        string json = JsonSerializer.Serialize(file, NeuralPackJsonContext.Default.NeuralPackFile);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        string checksum = ChecksumOf(payload);
        var stamped = new NeuralPackFile(Manifest with { Checksum = checksum }, Weights);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stamped, NeuralPackJsonContext.Default.NeuralPackFile));
    }

    private static void VerifyChecksum(string name, NeuralPackFile file)
    {
        var expected = file.Manifest.Checksum;
        if (string.IsNullOrWhiteSpace(expected))
            throw new PackValidationException($"Neural pack '{name}' has no checksum.");
        var unstamped = new NeuralPackFile(file.Manifest with { Checksum = "" }, file.Weights);
        string actual = ChecksumOf(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(unstamped, NeuralPackJsonContext.Default.NeuralPackFile)));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new PackValidationException($"Neural pack '{name}' is damaged (checksum mismatch).");
    }

    internal static string ChecksumOf(byte[] payload)
    {
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }
}

/// <summary>A pack that failed validation (§5.3): reported as a finding with fallback, never a crash.</summary>
public sealed class PackValidationException : Exception
{
    public PackValidationException(string message) : base(message) { }
    public PackValidationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The pack manifest (§5.2).</summary>
public sealed record PackManifest(
    int FormatVersion,
    string Architecture,
    int BeatSchemaVersion,
    string Name,
    string Author,
    string Description,
    List<PackStyle> Styles,
    List<string> Instruments,
    List<string> Pairs,
    PackInputs Inputs,
    Dictionary<string, long> MinGapTicks,
    Dictionary<string, double[]> DensityRange,
    List<string> Vocabulary,
    PackTrainingSummary Training,
    string License,
    string Checksum = "")
{
    public void Validate(string name)
    {
        if (FormatVersion is not (NeuralPack.FormatVersion or NeuralPack.PreviousFormatVersion))
            throw new PackValidationException($"Neural pack '{name}' is format {FormatVersion}; this app reads {NeuralPack.PreviousFormatVersion}–{NeuralPack.FormatVersion}.");
        if (BeatSchemaVersion != BeatPipeline.BeatSchemaVersion)
            throw new PackValidationException($"Neural pack '{name}' uses beat schema {BeatSchemaVersion}; this app uses {BeatPipeline.BeatSchemaVersion}. Retrain the model for the new schema.");
        if (!string.Equals(Architecture, BiGruCrf.ArchitectureName, StringComparison.OrdinalIgnoreCase))
            throw new PackValidationException($"Neural pack '{name}' is a '{Architecture}' model; this app runs '{BiGruCrf.ArchitectureName}'.");
        if (Styles.Count == 0)
            throw new PackValidationException($"Neural pack '{name}' names no styles.");
        if (Vocabulary.Count == 0)
            throw new PackValidationException($"Neural pack '{name}' has an empty vocabulary.");
        if (Pairs.Count == 0)
            throw new PackValidationException($"Neural pack '{name}' supports no source–target pairs.");
    }
}

/// <summary>A style label the pack was trained to imitate; id 0 is always Any (§3.3).</summary>
public sealed record PackStyle(int Id, string Label, string Description);

/// <summary>Which context features the pack uses, including whether it uses tier (§5.2).</summary>
public sealed record PackInputs(
    bool UseTier,
    bool UseStyle,
    int HiddenSize,
    int EmbeddingSize,
    string[] Targets,
    string[] Styles);

/// <summary>What the pack was trained on — counts and labels, never chart content (§6.3).</summary>
public sealed record PackTrainingSummary(
    int Songs,
    int Parts,
    string Studios,
    string Date,
    string TrainerVersion,
    string? Evaluation = null);

/// <summary>The trained weights: embedding, BiGRU, projection, CRF transitions.</summary>
public sealed class PackWeights
{
    public required float[] SourceEmbedding { get; init; }
    public required float[] StyleEmbedding { get; init; }
    public required float[] TargetEmbedding { get; init; }
    public required GruWeights Forward { get; init; }
    public required GruWeights Backward { get; init; }
    public required float[] OutputWeight { get; init; }
    public required float[] OutputBias { get; init; }
    public required float[] Transition { get; init; }
    public required float[] StartTransition { get; init; }
    public required float[] EndTransition { get; init; }

    public int ParameterCount =>
        SourceEmbedding.Length + StyleEmbedding.Length + TargetEmbedding.Length
        + Forward.ParameterCount + Backward.ParameterCount
        + OutputWeight.Length + OutputBias.Length + Transition.Length
        + StartTransition.Length + EndTransition.Length;

    public void Validate(string name, PackManifest manifest)
    {
        int vocab = manifest.Vocabulary.Count;
        int hidden = manifest.Inputs.HiddenSize;
        int outSize = hidden * 2;
        void Check(float[] values, int expected, string what)
        {
            if (values.Length != expected)
                throw new PackValidationException($"Neural pack '{name}' has {values.Length} {what}; expected {expected}.");
            foreach (float v in values)
            {
                if (!float.IsFinite(v))
                    throw new PackValidationException($"Neural pack '{name}' has a non-finite {what}.");
            }
        }

        int featureSize = BiGruCrf.FeatureSize(manifest);
        int styleCount = manifest.Styles.Count;
        int targetCount = manifest.Inputs.Targets.Length;
        Check(SourceEmbedding, vocab * manifest.Inputs.EmbeddingSize, "source embedding values");
        Check(StyleEmbedding, styleCount * manifest.Inputs.EmbeddingSize, "style embedding values");
        Check(TargetEmbedding, Math.Max(1, targetCount) * manifest.Inputs.EmbeddingSize, "target embedding values");
        Forward.Validate(name, featureSize, hidden, "forward GRU");
        Backward.Validate(name, featureSize, hidden, "backward GRU");
        Check(OutputWeight, outSize * vocab, "output weights");
        Check(OutputBias, vocab, "output biases");
        Check(Transition, vocab * vocab, "CRF transitions");
        Check(StartTransition, vocab, "CRF start transitions");
        Check(EndTransition, vocab, "CRF end transitions");
        if (ParameterCount > NeuralPack.MaxParameters)
            throw new PackValidationException($"Neural pack '{name}' has {ParameterCount} parameters; the limit is {NeuralPack.MaxParameters}.");
    }
}

/// <summary>One GRU layer's weights, laid out like PyTorch (spec §3.3 parity).</summary>
public sealed class GruWeights
{
    public required float[] Wz { get; init; }
    public required float[] Wr { get; init; }
    public required float[] Wh { get; init; }
    public required float[] Uz { get; init; }
    public required float[] Ur { get; init; }
    public required float[] Uh { get; init; }
    public required float[] BzIh { get; init; }
    public required float[] BzHh { get; init; }
    public required float[] BrIh { get; init; }
    public required float[] BrHh { get; init; }
    public required float[] BnIh { get; init; }
    public required float[] BnHh { get; init; }

    public int ParameterCount =>
        Wz.Length + Wr.Length + Wh.Length + Uz.Length + Ur.Length + Uh.Length
        + BzIh.Length + BzHh.Length + BrIh.Length + BrHh.Length + BnIh.Length + BnHh.Length;

    public void Validate(string name, int inputSize, int hidden, string what)
    {
        void Check(float[] values, int expected, string field)
        {
            if (values.Length != expected)
                throw new PackValidationException($"Neural pack '{name}' has {values.Length} {what} {field}; expected {expected}.");
            foreach (float v in values)
            {
                if (!float.IsFinite(v))
                    throw new PackValidationException($"Neural pack '{name}' has a non-finite {what} {field}.");
            }
        }

        Check(Wz, hidden * inputSize, nameof(Wz));
        Check(Wr, hidden * inputSize, nameof(Wr));
        Check(Wh, hidden * inputSize, nameof(Wh));
        Check(Uz, hidden * hidden, nameof(Uz));
        Check(Ur, hidden * hidden, nameof(Ur));
        Check(Uh, hidden * hidden, nameof(Uh));
        Check(BzIh, hidden, nameof(BzIh));
        Check(BzHh, hidden, nameof(BzHh));
        Check(BrIh, hidden, nameof(BrIh));
        Check(BrHh, hidden, nameof(BrHh));
        Check(BnIh, hidden, nameof(BnIh));
        Check(BnHh, hidden, nameof(BnHh));
    }
}

/// <summary>The pack file shape on disk.</summary>
public sealed record NeuralPackFile(PackManifest Manifest, PackWeights Weights);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(NeuralPackFile))]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(PackWeights))]
[JsonSerializable(typeof(GruWeights))]
[JsonSerializable(typeof(PackStyle))]
[JsonSerializable(typeof(PackInputs))]
[JsonSerializable(typeof(PackTrainingSummary))]
internal partial class NeuralPackJsonContext : JsonSerializerContext
{
}
