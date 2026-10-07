using System.Runtime.ExceptionServices;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Scanning;

/// <summary>Adds files to and removes files from a song folder.</summary>
public static class SongFolderFiles
{
    /// <summary>
    /// Deletes <paramref name="removes"/> from <paramref name="folder"/> and copies
    /// <paramref name="adds"/> into it. An added file replaces one of the same name.
    /// </summary>
    /// <remarks>
    /// <para>Names are relative to the folder, with <c>/</c> between segments. Every added file is
    /// copied to a temp file before anything else changes, so a copy that fails leaves the folder as
    /// it was, and a source may be one of the files being removed or replaced.</para>
    /// <para>The temp files are then moved into place, and only then are the removed files deleted.
    /// So in the worst case, a crash or failure between the moves and the deletes, the folder holds
    /// both the old files and the new ones; it's never left with neither. A removed file that only
    /// differs from an added one in letter case is renamed aside before the moves, because on a
    /// case-insensitive file system deleting it afterwards would delete the new file; it's put back
    /// if its add isn't moved into place, and deleted with the other removals otherwise. A removed
    /// file with the same name as an added one is left for the add to replace.</para>
    /// <para>A removed file that no longer exists is skipped. A delete that fails doesn't stop the
    /// others. Folders a removal empties are kept.</para>
    /// <para><paramref name="afterMoves"/>, when given, runs once every add is in place and before
    /// anything is deleted: a save writes <c>song.ini</c> there, so a removed chart is only deleted once
    /// <c>song.ini</c> holds what was taken from it. If it throws, nothing is deleted.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">A name is the folder's <c>song.ini</c>, which is edited as metadata, or points outside the folder.</exception>
    /// <exception cref="IOException">A file can't be read, written or deleted. The first failed delete is thrown once every delete has been tried.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before anything in the folder changed.</exception>
    public static async Task ApplyAsync(
        string folder,
        IReadOnlyCollection<string> removes,
        IReadOnlyList<AddedFile> adds,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? afterMoves = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(removes);
        ArgumentNullException.ThrowIfNull(adds);

        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var removePaths = removes.Select(name => Resolve(root, name)).ToList();
        var targets = adds.Select(file => Resolve(root, file.StoredName)).ToList();

        // A removal with an add's exact path is replaced by the add's move, so it isn't deleted at all.
        // One that differs only in case is set aside before the moves, and the rest deleted after them.
        var exactTargets = targets.ToHashSet(StringComparer.Ordinal);
        var caseTargets = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toSetAside = removePaths
            .Where(path => !exactTargets.Contains(path) && caseTargets.Contains(path))
            .Select(path => new SetAside(path, targets.FindIndex(t => t.Equals(path, StringComparison.OrdinalIgnoreCase)), $"{path}.{Guid.NewGuid():N}.tmp"))
            .ToList();
        var deleteLast = removePaths.Where(path => !caseTargets.Contains(path)).ToList();

        var temps = new List<string>(adds.Count);
        var setAside = new List<SetAside>(toSetAside.Count);
        int moved = 0;
        try
        {
            for (int i = 0; i < adds.Count; i++)
            {
                // A unique name, so an existing file is never overwritten by a temp file.
                string temp = $"{targets[i]}.{Guid.NewGuid():N}.tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                temps.Add(temp);

                await using var input = new FileStream(adds[i].SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            Step("staged");
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in toSetAside)
            {
                if (!File.Exists(file.Path))
                    continue;
                File.Move(file.Path, file.Aside);
                setAside.Add(file);
            }

            for (; moved < temps.Count; moved++)
            {
                File.Move(temps[moved], targets[moved], overwrite: true);
                Step("moved:" + adds[moved].StoredName);
            }
        }
        catch
        {
            // A temp that was moved is now the target, which is never deleted here.
            foreach (string temp in temps.Skip(moved))
                TryDelete(temp);

            // A file set aside goes back where it was, unless its add has taken its place.
            foreach (var file in setAside)
            {
                if (file.Add >= moved)
                    TryMove(file.Aside, file.Path);
                else
                    TryDelete(file.Aside);
            }

            throw;
        }

        if (afterMoves is not null)
        {
            try
            {
                await afterMoves(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The adds are in place, so what was set aside for them goes; the removals are kept.
                foreach (var file in setAside)
                    TryDelete(file.Aside);
                throw;
            }
        }

        Step("before-deletes");
        Exception? firstFailure = null;
        foreach (string path in deleteLast.Concat(setAside.Select(f => f.Aside)))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
            ExceptionDispatchInfo.Throw(firstFailure);
    }

    /// <summary>
    /// Called with each step as it's reached (<c>staged</c>, <c>moved:&lt;name&gt;</c>, <c>before-deletes</c>), so a
    /// test can see what a crash at that point would leave in the folder.
    /// </summary>
    internal static AsyncLocal<Action<string>?> StepReached { get; } = new();

    private static void Step(string step) => StepReached.Value?.Invoke(step);

    /// <summary>The full path of <paramref name="name"/> in the folder at <paramref name="root"/>.</summary>
    private static string Resolve(string root, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{name}' is edited as metadata, not added or removed as a file.", nameof(name));

        string path = Path.GetFullPath(Path.Combine([root, .. name.Split('/')]));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length == root.Length)
            throw new ArgumentException($"'{name}' isn't inside the song folder.", nameof(name));
        return path;
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A removed file that only differs from an added one in letter case, renamed aside while the adds move into place.</summary>
    /// <param name="Path">Where it is.</param>
    /// <param name="Add">The index of the add it differs from.</param>
    /// <param name="Aside">Where it's put while the adds move.</param>
    private readonly record struct SetAside(string Path, int Add, string Aside);
}
