using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChartPacker.Core.Settings;

/// <summary>Reading and writing the per-user JSON files that several processes may share (charter-spec §10).</summary>
internal static class JsonFiles
{
    /// <summary>The file's JSON object, or null when it's missing, unreadable or not an object.</summary>
    /// <param name="warn">Whether to trace why a file that's there couldn't be read.</param>
    public static JsonObject? ReadObject(string path, bool warn)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (warn)
                Trace.TraceWarning($"Ignoring unreadable file '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="json"/> to a temp file of its own beside <paramref name="path"/> and renames it
    /// into place, so a reader never sees half a file and two writers don't share a temp file. Failures are
    /// traced, and give false.
    /// </summary>
    public static bool WriteAtomically(string path, JsonNode json)
    {
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = File.Create(tempPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteTo(writer);
            }
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Couldn't write '{path}': {ex.Message}");
            try { File.Delete(tempPath); } catch (Exception) { }
            return false;
        }
    }
}
