namespace ChartShared.Core.Generation;

/// <summary>A generator model in storage: its kind and its name within that kind.</summary>
/// <param name="Type">What kind of generator the model belongs to.</param>
/// <param name="Name">The model's name within its kind, without the kind prefix or extension.</param>
public sealed record GeneratorModelId(GeneratorType Type, string Name);
