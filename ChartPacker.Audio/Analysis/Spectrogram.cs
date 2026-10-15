namespace ChartPacker.Audio.Analysis;

/// <summary>
/// A stem's (or a mix's) spectrogram (charter-spec C13), held in memory only: a column of 256 rows
/// (<see cref="SpectrogramRows"/>) every 256 samples, each row a byte of level. It's computed in chunks by
/// <see cref="SpectrogramBuilder"/>, near <see cref="Focus"/> first, and can be drawn from while it fills in.
/// </summary>
/// <remarks>
/// <para>Column <c>c</c> is a 2048-sample Hann window centred on sample <c>c × 256</c>. Its bytes hold absolute level,
/// <see cref="FloorDb"/> + <see cref="DbPerStep"/> × byte, where 0 dB is a full-scale sine; contrast is for drawing to
/// apply. 0 is silence (or below −120 dB).</para>
/// <para>Any thread may read it. A column's bytes are written before it's counted ready, and read after.</para>
/// </remarks>
public sealed class Spectrogram : IDisposable
{
    public const int WindowSize = 2048;
    public const int Hop = 256;
    public const int Rows = SpectrogramRows.Count;
    public const double FloorDb = -120;
    public const double DbPerStep = 0.5;

    private readonly byte[]?[] _blocks;
    private readonly int[] _ready;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _version;
    private int _released;
    private volatile string? _error;

    internal Spectrogram(int sampleRate, long lengthFrames, int chunkColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkColumns);
        SampleRate = sampleRate;
        ColumnCount = (int)(Math.Max(0, lengthFrames) / Hop) + 1;
        ChunkColumns = chunkColumns;
        ChunkCount = (ColumnCount + chunkColumns - 1) / chunkColumns;
        _blocks = new byte[ChunkCount][];
        _ready = new int[ChunkCount];
    }

    /// <summary>Raised on a worker thread each time more columns are ready, and when it's finished. Keep handlers short.</summary>
    public event Action<Spectrogram>? Updated;

    public int SampleRate { get; }

    public int ColumnCount { get; }

    /// <summary>How many columns are computed together; a chunk is ready all at once or column by column from its start.</summary>
    public int ChunkColumns { get; }

    public int ChunkCount { get; }

    public double SecondsPerColumn => (double)Hop / SampleRate;

    /// <summary>The bytes it holds once computed, which count against <see cref="SpectrogramCache"/>'s budget from the start.</summary>
    public long SizeBytes => (long)ColumnCount * Rows;

    /// <summary>Goes up each time more columns are ready.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Every column is ready.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Finishes when every column is ready, the audio can't be read, or it's released. It never faults.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Why some or all of the audio couldn't be read, or <see langword="null"/>.</summary>
    public string? Error => _error;

    /// <summary>It's been released: its columns are gone and no more will be computed.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    internal SpectrogramJob? Job { get; set; }

    /// <summary>The byte for a level of <paramref name="decibels"/>.</summary>
    public static byte LevelOf(double decibels) => (byte)Math.Clamp(Math.Round((decibels - FloorDb) / DbPerStep), 0, 255);

    /// <summary>The level a byte stands for, in dB.</summary>
    public static double DecibelsOf(byte level) => FloorDb + (level * DbPerStep);

    /// <summary>The time column <paramref name="column"/> is centred on, in seconds of its audio.</summary>
    public double SecondsOf(int column) => (double)column * Hop / SampleRate;

    /// <summary>The column nearest <paramref name="seconds"/>, within the columns there are.</summary>
    public int ColumnAt(double seconds) => (int)Math.Clamp(Math.Round(seconds * SampleRate / Hop), 0, ColumnCount - 1);

    public bool IsReady(int column)
    {
        if ((uint)column >= (uint)ColumnCount)
            return false;
        int chunk = column / ChunkColumns;
        return column - (chunk * ChunkColumns) < Volatile.Read(ref _ready[chunk]);
    }

    /// <summary>How many of chunk <paramref name="chunk"/>'s columns are ready, from its first.</summary>
    public int ReadyIn(int chunk) => (uint)chunk < (uint)ChunkCount ? Volatile.Read(ref _ready[chunk]) : 0;

    /// <summary>Every column of chunk <paramref name="chunk"/> is ready.</summary>
    public bool IsChunkReady(int chunk) =>
        (uint)chunk < (uint)ChunkCount && Volatile.Read(ref _ready[chunk]) == ColumnsIn(chunk);

    /// <summary>Column <paramref name="column"/>'s rows, lowest first; empty until it's ready, and once released.</summary>
    public ReadOnlySpan<byte> Column(int column)
    {
        if (!IsReady(column))
            return [];
        int chunk = column / ChunkColumns;
        var block = Volatile.Read(ref _blocks[chunk]);
        return block is null ? [] : block.AsSpan((column - (chunk * ChunkColumns)) * Rows, Rows);
    }

    /// <summary>Has what's near <paramref name="seconds"/> computed next, and prefers this over other spectrograms.</summary>
    public void Focus(double seconds) => Job?.Focus(ColumnAt(seconds) / ChunkColumns);

    /// <summary>Releases its columns and stops computing it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        Job?.Cancel();
        for (int i = 0; i < _blocks.Length; i++)
            Volatile.Write(ref _blocks[i], null);
        _completion.TrySetResult();
    }

    internal int ColumnsIn(int chunk) => Math.Min(ChunkColumns, ColumnCount - (chunk * ChunkColumns));

    /// <summary>The bytes to write chunk <paramref name="chunk"/>'s columns into; <see langword="null"/> once released.</summary>
    internal byte[]? BlockFor(int chunk)
    {
        if (IsReleased)
            return null;
        var block = new byte[ColumnsIn(chunk) * Rows];
        Volatile.Write(ref _blocks[chunk], block);
        return IsReleased ? null : block;
    }

    /// <summary>Counts the first <paramref name="columns"/> of chunk <paramref name="chunk"/> ready.</summary>
    internal void Publish(int chunk, int columns)
    {
        Volatile.Write(ref _ready[chunk], columns);
        Interlocked.Increment(ref _version);
        Updated?.Invoke(this);
    }

    internal void Finish(string? error)
    {
        _error = error;
        IsComplete = !IsReleased && Enumerable.Range(0, ChunkCount).All(IsChunkReady);
        _completion.TrySetResult();
        Updated?.Invoke(this);
    }
}
