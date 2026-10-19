using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChartShared.Core.Settings;

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

    // How long Update waits for another process's update of the same file, which holds its lock for a moment.
    private const int LockAttempts = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Reads the file at <paramref name="path"/>, has <paramref name="change"/> change it, and writes what it gives with
    /// <see cref="WriteAtomically"/>, holding <c>{path}.lock</c> from the read to the rename, so that another process's
    /// update can't come between them and be lost.
    /// </summary>
    /// <param name="change">
    /// Given the file's object, or null when it's missing or unreadable; gives what to write, or null to write nothing.
    /// </param>
    /// <returns>Whether it was written: false when <paramref name="change"/> gave null, or the lock or the file couldn't be had.</returns>
    /// <remarks>
    /// The lock is the open handle, never the file, so one left by a process that died means nothing. On Windows the file
    /// is deleted as it's closed; elsewhere it's left, as deleting a file another process is waiting to lock would let two
    /// hold it.
    /// </remarks>
    public static bool Update(string path, Func<JsonObject?, JsonObject?> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var held = Lock(path);
        if (held is null)
            return false;

        return change(ReadObject(path, warn: false)) is { } json && WriteAtomically(path, json);
    }

    // The lock on the file at path, held until it's disposed; null when another process held it throughout, or it
    // couldn't be made.
    private static FileStream? Lock(string path)
    {
        string lockPath = path + ".lock";
        Exception? failure = null;
        for (int attempt = 1; attempt <= LockAttempts; attempt++)
        {
            if (attempt > 1)
                Thread.Sleep(LockRetryDelay);

            try
            {
                if (attempt == 1)
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another process, or being let go of by one.
                failure = ex;
            }
        }

        Trace.TraceWarning($"Couldn't lock '{path}' to write it: {failure?.Message}");
        return null;
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
