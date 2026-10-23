namespace ChartShared.Core.Scanning;

/// <summary>
/// Replaces and deletes a song's files, waiting a moment for another process to let go of them (charter-spec §4.8).
/// </summary>
/// <remarks>
/// On Windows a file can't be replaced or deleted while another process has it open, even only for reading. Another
/// Chart Packer lets go of a song's files once it sees the song's <see cref="SongLock"/>, within a second or so, and
/// other readers (a thumbnail, a virus scan) hold a file only for a moment, so a replace or delete that's refused is
/// tried again for a few seconds before it gives up.
/// </remarks>
public static class SongFileCommit
{
    /// <summary>How long a replace or delete is tried for, by default.</summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(5);

    // How long to wait between tries.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a replace or delete given no patience of its own is tried for, so a test can wait less.</summary>
    internal static AsyncLocal<TimeSpan?> PatienceOverride { get; } = new();

    /// <summary>How long a replace or delete given no patience of its own is tried for.</summary>
    internal static TimeSpan Patience => PatienceOverride.Value ?? DefaultPatience;

    /// <summary>Moves <paramref name="source"/> to <paramref name="target"/>, replacing it.</summary>
    /// <param name="patience">How long to keep trying while <paramref name="target"/> is in use; <see cref="DefaultPatience"/> when not given.</param>
    /// <exception cref="IOException">The move failed, or the target was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, or the target was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while waiting.</exception>
    public static void MoveOver(string source, string target, CancellationToken cancellationToken = default, TimeSpan? patience = null) =>
        Retry(target, () => File.Move(source, target, overwrite: true), patience ?? Patience, cancellationToken);

    /// <inheritdoc cref="MoveOver"/>
    /// <remarks>Waits without holding a thread.</remarks>
    public static Task MoveOverAsync(string source, string target, CancellationToken cancellationToken = default, TimeSpan? patience = null) =>
        RetryAsync(target, () => File.Move(source, target, overwrite: true), patience ?? Patience, cancellationToken);

    /// <summary>Moves <paramref name="source"/> to <paramref name="target"/>, which isn't there, as for a song's file set aside.</summary>
    /// <param name="patience">How long to keep trying while <paramref name="source"/> is in use; <see cref="DefaultPatience"/> when not given.</param>
    /// <exception cref="IOException">The move failed, or the source was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, or the source was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while waiting.</exception>
    public static void Move(string source, string target, CancellationToken cancellationToken = default, TimeSpan? patience = null) =>
        Retry(source, () => File.Move(source, target), patience ?? Patience, cancellationToken);

    /// <summary>Deletes <paramref name="path"/>, if it's there.</summary>
    /// <param name="patience">How long to keep trying while the file is in use; <see cref="DefaultPatience"/> when not given.</param>
    /// <exception cref="IOException">The file was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied, or the file was still in use once <paramref name="patience"/> ran out.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while waiting.</exception>
    public static void Delete(string path, CancellationToken cancellationToken = default, TimeSpan? patience = null) =>
        Retry(path, () => File.Delete(path), patience ?? Patience, cancellationToken);

    /// <inheritdoc cref="Delete"/>
    /// <remarks>Waits without holding a thread.</remarks>
    public static Task DeleteAsync(string path, CancellationToken cancellationToken = default, TimeSpan? patience = null) =>
        RetryAsync(path, () => File.Delete(path), patience ?? Patience, cancellationToken);

    private static void Retry(string path, Action action, TimeSpan patience, CancellationToken cancellationToken)
    {
        DateTime giveUp = DateTime.UtcNow + patience;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (InUse(e, path, giveUp))
            {
            }

            if (cancellationToken.WaitHandle.WaitOne(RetryDelay))
                cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task RetryAsync(string path, Action action, TimeSpan patience, CancellationToken cancellationToken)
    {
        DateTime giveUp = DateTime.UtcNow + patience;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (InUse(e, path, giveUp))
            {
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    // Whether e, from moving or deleting path, says it's in use, while there's still time to wait: Windows says so as
    // a sharing violation, or as access denied for a file being replaced.
    private static bool InUse(Exception e, string path, DateTime giveUp) =>
        e is IOException or UnauthorizedAccessException
        && e is not (FileNotFoundException or DirectoryNotFoundException)
        && File.Exists(path)
        && DateTime.UtcNow < giveUp;
}
