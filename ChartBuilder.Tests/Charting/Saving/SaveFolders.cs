using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.Tests.Charting.Saving;

/// <summary>Songs on disk for the save tests to open and save, in a temp folder of the test's own.</summary>
internal sealed class SaveFolders : IDisposable
{
    private const int SampleRate = 8000;

    public SaveFolders() => Root = Directory.CreateTempSubdirectory("chartbuilder-save-test-").FullName;

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A song folder named <paramref name="name"/> holding <paramref name="files"/>.</summary>
    public string Song(string name, params (string Name, byte[] Bytes)[] files)
    {
        string folder = Path.Combine(Root, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, bytes) in files)
            System.IO.File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return folder;
    }

    /// <summary>A file in the root, outside any song.</summary>
    public string Loose(string name, byte[] bytes)
    {
        string path = Path.Combine(Root, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public static byte[] Ini(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", ["[song]", .. lines]) + "\r\n");

    /// <summary>The <c>song.ini</c> lines that read a chart with <paramref name="options"/>.</summary>
    public static string[] IniLinesOf(ChartReadOptions options)
    {
        var lines = new List<string>();
        if (options.ProDrums is { } pro)
            lines.Add($"pro_drums = {pro}");
        if (options.FiveLaneDrums is { } five)
            lines.Add($"five_lane_drums = {five}");
        if (options.SustainCutoff is { } cutoff)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"sustain_cutoff_threshold = {cutoff}"));
        if (options.HopoFrequency is { } hopo)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"hopo_frequency = {hopo}"));
        if (options.EighthNoteHopo is { } eighth)
            lines.Add($"eighthnote_hopo = {eighth}");
        if (options.MultiplierNote is { } multiplier)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"multiplier_note = {multiplier}"));
        return [.. lines];
    }

    /// <summary>A silent mono WAV <paramref name="ms"/> long.</summary>
    public static byte[] Wav(int ms)
    {
        int dataLength = SampleRate * ms / 1000 * 2;
        byte[] wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        return wav;
    }

    /// <summary>Opens the song at <paramref name="path"/> as the editor does.</summary>
    public static async Task<OpenedSong> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var result = await SongOpener.OpenAsync(SongOpener.EntryOf(path)!, cancellationToken);
        Assert.IsNull(result.Problem, $"{result.Problem?.Kind}: {result.Problem?.Detail}");
        return result.Song!;
    }

    /// <summary>
    /// Saves <paramref name="song"/> as the editor's Save does: every pending change but the reverted ones, the chart
    /// checked with its song events placed (as without audio), then written through the song's draft. With
    /// <paramref name="stash"/>, the files the save replaces or removes are kept there first, as the editor keeps them for
    /// undo. Fails the test if it doesn't go through.
    /// </summary>
    public static async Task<ChartSaveRequest> SaveAsync(EditorSong song, CancellationToken cancellationToken, string? removeChart = null, MediaStash? stash = null)
    {
        var request = ChartSaver.RequestFor(song, ToWrite(song), song.PendingChanges(), removeChart);
        var written = ChartSaver.Check(request);
        Assert.IsTrue(written.IsValid, Describe(written));
        var slots = request.Media!.Keys.ToList();
        var kept = stash is null ? new Dictionary<MediaSlot, string>() : await stash.KeepAsync(song, slots, cancellationToken);
        var findings = await ChartSaver.SaveAsync(song.Entry!, request, written, cancellationToken);
        Assert.DoesNotContain(f => f.Severity == ChartPacker.Core.Validation.Severity.Error, findings, string.Join("; ", findings.Select(f => f.Message)));
        song.SongEventsSaved(request.Snapshot);
        song.Saved(ChartSaver.SavedSong(song, song.Entry!, request), song.Chart.Parts.Select(p => p.Instrument).ToHashSet(), new MediaSaveResult(slots, kept));
        return request;
    }

    /// <summary>The chart a save of <paramref name="song"/> writes: its own, with the song events placed as without audio.</summary>
    public static EditableChart ToWrite(EditorSong song) =>
        SongEventPlacement.WithSongEvents(song.Chart, SongEventPlacement.Place(song.Chart, null, song.Delay));

    public static string Describe(ChartWriteResult written) =>
        $"{string.Join("; ", written.ModelDifferences.Select(d => d.Description))} {GameViewTests.Describe(written.GameDifferences)} {string.Join("; ", written.ReloadIssues)}";

    /// <summary>The bytes of <paramref name="name"/> in the song at <paramref name="entry"/>.</summary>
    public static async Task<byte[]> ReadAsync(SongEntry entry, string name, CancellationToken cancellationToken) =>
        (await SongFile.ReadAsync(entry, name, long.MaxValue, cancellationToken))!;
}
