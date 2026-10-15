using System.Diagnostics;
using System.Globalization;

namespace ChartShared.Core.Diagnostics;

/// <summary>
/// A file saying an app is running, removed when it shuts down, so the next start can tell when a session never did:
/// it crashed, was ended, or the computer stopped. Some crashes leave nothing else behind, e.g. an
/// <see cref="ExecutionEngineException"/> or an access violation in native code, which no handler sees.
/// </summary>
/// <remarks>
/// Each process has a file of its own, <c>&lt;app&gt;.&lt;process id&gt;.running</c>, so a second copy of an app running
/// at once neither takes the first's for a crash nor removes it.
/// </remarks>
public sealed class SessionMarker
{
    private const string Extension = ".running";

    private readonly string _path;

    private SessionMarker(string path, IReadOnlyList<UncleanSession> unclean)
    {
        _path = path;
        Unclean = unclean;
    }

    /// <summary>Earlier sessions of the app that never shut down, oldest first. Their markers are removed.</summary>
    public IReadOnlyList<UncleanSession> Unclean { get; }

    /// <summary>
    /// Finds the app's earlier sessions that never shut down, then marks this one as running, in
    /// <paramref name="folder"/>, which is created if need be.
    /// </summary>
    /// <param name="isRunning">
    /// Whether a session's process is still running, e.g. a second copy of the app; such a session isn't unclean. By
    /// default the process with that id is looked for, and must have started when the session did.
    /// </param>
    /// <exception cref="IOException">The folder or the marker can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public static SessionMarker Begin(
        string folder,
        string appName,
        string version,
        DateTimeOffset started,
        int processId,
        Func<UncleanSession, bool>? isRunning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentNullException.ThrowIfNull(version);
        isRunning ??= IsProcessRunning;

        Directory.CreateDirectory(folder);
        var unclean = new List<UncleanSession>();
        foreach (string path in Directory.EnumerateFiles(folder, appName + ".*" + Extension))
        {
            var session = Read(path);
            if (session is null || session.ProcessId == processId || isRunning(session))
                continue;

            unclean.Add(session);
            TryDelete(path);
        }

        unclean.Sort((a, b) => a.Started.CompareTo(b.Started));
        string own = Path.Combine(folder, $"{appName}.{processId.ToString(CultureInfo.InvariantCulture)}{Extension}");
        File.WriteAllLines(own, [processId.ToString(CultureInfo.InvariantCulture), started.ToString("O", CultureInfo.InvariantCulture), version]);
        return new SessionMarker(own, unclean);
    }

    /// <summary>Marks the session as shut down. Safe to call more than once.</summary>
    public void End() => TryDelete(_path);

    // A marker is its process id, when it started, and the app's version, a line each. One that can't be read (cut
    // short by the crash, say) still says a session didn't shut down, with what of it could be read.
    private static UncleanSession? Read(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        int processId = lines.Length > 0 && int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : 0;
        var started = lines.Length > 1 && DateTimeOffset.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        string version = lines.Length > 2 ? lines[2] : "unknown";
        return new UncleanSession(started, version, processId);
    }

    // The process id may since have been given to another program, so it must also have started when the session did.
    private static bool IsProcessRunning(UncleanSession session)
    {
        if (session.ProcessId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            var startedAt = new DateTimeOffset(process.StartTime);
            return !process.HasExited && (startedAt - session.Started).Duration() < TimeSpan.FromMinutes(1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not running, or another user's, which this app's sessions never are.
            return false;
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
            // Left for the next start to find again; it's only a hint.
        }
    }
}

/// <summary>A session of an app that never shut down.</summary>
/// <param name="Started">When it started.</param>
/// <param name="Version">The app's version then.</param>
/// <param name="ProcessId">Its process id, or 0 when the marker couldn't be read.</param>
public sealed record UncleanSession(DateTimeOffset Started, string Version, int ProcessId);
