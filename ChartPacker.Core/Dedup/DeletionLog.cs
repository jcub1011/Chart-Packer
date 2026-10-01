using System.Text.Json;
using ChartPacker.Core.Trash;

namespace ChartPacker.Core.Dedup;

/// <summary>
/// An append-only record of every copy the duplicate finder deleted or tried to, one JSON object
/// per line, so a mistake can be traced to where the copy went.
/// </summary>
/// <param name="path">The log file. It and its folder are created as needed.</param>
public sealed class DeletionLog(string path)
{
    /// <summary>The log file.</summary>
    public string FilePath { get; } = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    /// <summary>Opens the log for one run of deletions and writes a line marking its start.</summary>
    /// <exception cref="IOException">The log can't be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    public DeletionLogSession Open(TrashKind trash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var session = new DeletionLogSession(stream, Guid.NewGuid().ToString("N"), trash);
        try
        {
            session.WriteStart();
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }
}

/// <summary>One run of deletions being written to a <see cref="DeletionLog"/>.</summary>
public sealed class DeletionLogSession : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _session;
    private readonly TrashKind _trash;

    internal DeletionLogSession(FileStream stream, string session, TrashKind trash)
    {
        _stream = stream;
        _session = session;
        _trash = trash;
    }

    /// <summary>Writes the outcome for one copy, and flushes it to disk.</summary>
    /// <exception cref="IOException">The log can't be written.</exception>
    public void Write(DeletionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        WriteLine(json =>
        {
            json.WriteString("action", outcome.Status.ToString());
            json.WriteString("tier", outcome.Item.Tier.ToString());
            json.WriteString("victim", outcome.Item.Victim.FullPath);
            json.WriteString("kind", outcome.Item.Victim.Kind.ToString());
            json.WriteNumber("bytes", outcome.Item.Bytes);
            json.WriteString("keeper", outcome.Item.Keeper.Path);
            if (outcome.Reason != DeletionSkipReason.None)
                json.WriteString("reason", outcome.Reason.ToString());
            if (outcome.Error is not null)
                json.WriteString("error", outcome.Error);
        });
    }

    public void Dispose() => _stream.Dispose();

    internal void WriteStart() => WriteLine(json => json.WriteString("action", "Start"));

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        using (var json = new Utf8JsonWriter(_stream))
        {
            json.WriteStartObject();
            json.WriteString("t", DateTime.UtcNow);
            json.WriteString("session", _session);
            json.WriteString("trash", _trash.ToString());
            body(json);
            json.WriteEndObject();
        }

        _stream.WriteByte((byte)'\n');
        _stream.Flush(flushToDisk: true);
    }
}
