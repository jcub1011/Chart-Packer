using System.Diagnostics;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Sng;
using Microsoft.Win32.SafeHandles;

namespace ScanBench;

/// <summary>Reads every song one at a time, timing each step the way <see cref="SongSummaryReader"/> takes it.</summary>
internal static class Phases
{
    public static void Run(string root, int depth, bool warm)
    {
        // A first, untimed pass takes the JIT out of the times, and warms the file cache.
        if (warm)
            new Pass().Read(root, depth);

        new Pass().Read(root, depth).Print();
    }

    private sealed class Pass
    {
        private readonly Dictionary<string, TimeSpan> _times = [];
        private readonly List<StemRead> _stems = [];
        private int _songs, _sngs, _folders, _failed, _fallbacks;

        public Pass Read(string root, int depth)
        {
            var watch = Stopwatch.StartNew();
            var entries = SongLibraryScanner.Enumerate(root, depth).ToList();
            Add("search", watch.Elapsed);

            _songs = entries.Count;
            foreach (var entry in entries)
            {
                try
                {
                    bool measured;
                    if (entry.Kind == SongEntryKind.SngFile)
                    {
                        _sngs++;
                        measured = ReadSng(entry.FullPath);
                    }
                    else
                    {
                        _folders++;
                        measured = ReadFolder(entry.FullPath);
                    }

                    if (measured)
                        _fallbacks++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _failed++;
                }
            }

            return this;
        }

        public void Print()
        {
            var total = _times.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            Console.WriteLine($"{_songs} songs: {_sngs} .sng, {_folders} folders, {_failed} unreadable; {_fallbacks} without a song_length.");
            Console.WriteLine();
            Console.WriteLine("| Step | Time (s) | Share |");
            Console.WriteLine("|---|--:|--:|");
            foreach (var (step, time) in _times.OrderByDescending(t => t.Value))
                Console.WriteLine($"| {step} | {time.TotalSeconds:F2} | {time / total:P0} |");
            Console.WriteLine($"| total | {total.TotalSeconds:F2} | |");

            if (_stems.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine("What ATL read to measure each stem, by format:");
            Console.WriteLine();
            Console.WriteLine("| Format | Stems | Reads/stem | KiB/stem | Jumps/stem | Furthest (median) | ms/stem |");
            Console.WriteLine("|---|--:|--:|--:|--:|--:|--:|");
            foreach (var group in _stems.GroupBy(s => s.Format).OrderByDescending(g => g.Count()))
            {
                var reaches = group.Select(s => s.Reach).Order().ToList();
                Console.WriteLine(
                    $"| {group.Key} | {group.Count()} | {group.Average(s => s.Calls):F0} | {group.Average(s => s.Bytes) / 1024:F0} | " +
                    $"{group.Average(s => s.Jumps):F1} | {reaches[reaches.Count / 2]:P0} | {group.Average(s => s.Time.TotalMilliseconds):F1} |");
            }
        }

        /// <returns>Whether the song had no song_length, so its audio was measured.</returns>
        private bool ReadSng(string path)
        {
            var watch = Stopwatch.StartNew();
            var read = SngReader.Read(path);
            var pairs = read.Metadata.Select(p => KeyValuePair.Create(p.Key, p.Value)).ToList();
            _ = new FileInfo(path).Length;
            Add("sng index", watch.Elapsed);

            if (read.Header is not { } header)
                return false;

            if (ChartReader.Pick(read.Files.Select(f => f.Name)) is { } name)
            {
                var file = read.Files.First(f => f.Name == name);
                watch.Restart();
                byte[]? bytes;
                try
                {
                    bytes = SongFile.ReadFromSng(path, header, file, ChartReader.MaxBytes, CancellationToken.None);
                }
                catch (IOException)
                {
                    bytes = null;
                }

                Add("chart read", watch.Elapsed);
                Parse(bytes, name, pairs);
            }

            if (SongHeadline.From(pairs).LengthMs is not null)
                return false;

            watch.Restart();
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (var file in read.Files.Where(f => AudioLength.IsAudioFile(f.Name)))
            {
                try
                {
                    Measure(new SngEntryStream(handle, file, header.Mask.Span), file.Name);
                }
                catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
                {
                }
            }

            Add("audio length", watch.Elapsed);
            return true;
        }

        /// <returns>Whether the song had no song_length, so its audio was measured.</returns>
        private bool ReadFolder(string folder)
        {
            var watch = Stopwatch.StartNew();
            var files = SongFolderListing.List(folder);
            var storedNames = files.Select(f => f.RelativePath).Where(n => !n.Contains('/')).ToList();
            Add("folder listing", watch.Elapsed);

            watch.Restart();
            var songIni = files.FirstOrDefault(f => f.RelativePath.Equals(SongIni.FileName, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<KeyValuePair<string, string>> pairs = songIni is null
                ? []
                : SongIni.ReadAsync(Path.Combine(folder, songIni.RelativePath)).GetAwaiter().GetResult().Pairs;
            Add("song.ini", watch.Elapsed);

            if (ChartReader.Pick(storedNames) is { } name)
            {
                watch.Restart();
                var bytes = SongFile.ReadFileAsync(Path.Combine(folder, name), ChartReader.MaxBytes, CancellationToken.None).GetAwaiter().GetResult();
                Add("chart read", watch.Elapsed);
                Parse(bytes, name, pairs);
            }

            if (SongHeadline.From(pairs).LengthMs is not null)
                return false;

            watch.Restart();
            foreach (var file in files.Where(f => AudioLength.IsAudioFile(f.RelativePath)))
            {
                try
                {
                    // Unbuffered, so the counts are what ATL asks for.
                    Measure(new FileStream(Path.Combine(folder, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1), file.RelativePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Add("audio length", watch.Elapsed);
            return true;
        }

        private void Parse(byte[]? bytes, string name, IReadOnlyList<KeyValuePair<string, string>> pairs)
        {
            if (bytes is null)
                return;

            var watch = Stopwatch.StartNew();
            _ = ChartContents.Read(bytes, name, ChartReadOptions.From(pairs));
            Add("chart parse", watch.Elapsed);
        }

        private void Measure(Stream inner, string name)
        {
            var watch = Stopwatch.StartNew();
            using var stream = new CountingStream(inner);
            _ = AudioLength.MeasureMs(stream, name);
            _stems.Add(new StemRead(Path.GetExtension(name), stream.Calls, stream.Bytes, stream.Jumps, stream.Reach, watch.Elapsed));
        }

        private void Add(string step, TimeSpan time) => _times[step] = _times.GetValueOrDefault(step) + time;
    }

    private sealed record StemRead(string Format, long Calls, long Bytes, long Jumps, double Reach, TimeSpan Time);
}
