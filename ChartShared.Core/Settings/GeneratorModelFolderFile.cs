using System.Text.Json.Nodes;

namespace ChartShared.Core.Settings;

/// <summary>
/// <c>generator-model-settings.json</c>: where the generator models are stored (issue #154).
/// It holds <c>{ "Folder": "…" }</c>; a missing, blank or unreadable folder means the default,
/// <see cref="UserConfigPaths.GeneratorModelsFolder"/>. Both apps read and write it, like
/// <c>reduction-rules.json</c>.
/// </summary>
/// <remarks>The path is always passed in; tests pass a temp file.</remarks>
public sealed class GeneratorModelFolderFile
{
    private const string Folder = "Folder";

    private readonly string _path;

    public GeneratorModelFolderFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The stored folder, or null for the default.</summary>
    public string? LoadFolder() => Of(JsonFiles.ReadObject(_path, warn: true)?[Folder]);

    /// <summary>Stores <paramref name="folder"/>, or back to the default when it's null or blank.</summary>
    /// <returns>Whether the file was written.</returns>
    public bool SaveFolder(string? folder) => JsonFiles.Update(_path, file =>
    {
        file ??= [];
        if (string.IsNullOrWhiteSpace(folder))
            file.Remove(Folder);
        else
            file[Folder] = folder;
        return file;
    });

    private static string? Of(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            && value.GetValue<string>() is { } folder && !string.IsNullOrWhiteSpace(folder)
            ? folder
            : null;
}
