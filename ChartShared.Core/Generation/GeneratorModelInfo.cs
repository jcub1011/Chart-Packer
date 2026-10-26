namespace ChartShared.Core.Generation;

/// <summary>A stored generator model: its kind, path, size and last write.</summary>
/// <param name="Id">The model's kind and name.</param>
/// <param name="Path">The model's full file path.</param>
/// <param name="Size">The file's size in bytes, or -1 when it couldn't be read.</param>
/// <param name="Modified">When the file was last written.</param>
public sealed record GeneratorModelInfo(GeneratorModelId Id, string Path, long Size, DateTimeOffset Modified);
