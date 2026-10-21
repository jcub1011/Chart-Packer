using System.Globalization;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;

namespace DifficultyCalibration;

/// <summary>
/// A rated part of a song, read once: its highest charted difficulty as the games play it, one of its ratings, and the
/// model that rates it. Pro drums are a part for <c>diff_drums</c> and another for <c>diff_drums_real</c>.
/// </summary>
internal sealed record Part(
    string Song,
    string Artist,
    string Title,
    string Charter,
    bool Official,
    bool HeldOut,
    ChartInstrument Instrument,
    string Key,
    DifficultyInstrument Model,
    ChartDifficulty Difficulty,
    int Reported,
    NoteTrack Track,
    TempoMap Tempo,
    long? EndTick,
    IReadOnlyList<Phrase> FreeLanes)
{
    /// <summary>A short name for the rating: the part, or "ProDrums" for <c>diff_drums_real</c>.</summary>
    public string Name => Model == DifficultyInstrument.ProDrums ? "ProDrums" : Instrument.ToString();
}

/// <summary>Reads every rated part under a folder, without duplicates.</summary>
internal static class Library
{
    public static readonly ChartInstrument[] Instruments = Enum.GetValues<ChartInstrument>();

    // Charters whose charts shipped in the games, matched anywhere in the charter field.
    private static readonly string[] OfficialCharters =
    [
        "harmonix", "neversoft", "freestylegames", "vicarious visions", "budcat", "redoctane", "beenox", "activision",
    ];

    public static async Task<List<Part>> LoadAsync(string root, int depth)
    {
        // A song's own folder or .sng reads as that song alone; any other folder is searched.
        bool single = File.Exists(root) || File.Exists(Path.Combine(root, "song.ini"));
        var entries = single ? [SongOpener.EntryOf(root)!] : SongLibraryScanner.Enumerate(root, depth).ToList();
        var found = new List<Part>?[entries.Count];
        int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, async (index, token) =>
        {
            found[index] = await PartsOfAsync(entries[index], token);
            int now = Interlocked.Increment(ref done);
            if (now % 200 == 0)
                Console.Error.WriteLine($"  read {now}/{entries.Count}");
        });

        // The same chart as a folder and as a .sng: the first kept.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<Part>();
        int duplicates = 0;
        foreach (var part in found.Where(static f => f is not null).SelectMany(static f => f!))
        {
            if (seen.Add($"{Key(part.Artist)}|{Key(part.Title)}|{Key(part.Charter)}|{part.Instrument}|{part.Key}"))
                parts.Add(part);
            else
                duplicates++;
        }

        Console.Error.WriteLine($"{entries.Count} songs, {parts.Count} rated parts, {duplicates} duplicate parts left out.");
        return parts;
    }

    private static async Task<List<Part>?> PartsOfAsync(SongEntry entry, CancellationToken token)
    {
        try
        {
            var result = await SongOpener.OpenAsync(entry, token);
            if (result.Song is not { } song)
                return null;
            string artist = song.Value("artist") ?? "";
            string title = song.Value("name") ?? entry.DisplayName;
            string charter = song.Value("charter") ?? song.Value("frets") ?? "";
            bool official = OfficialCharters.Any(c => charter.Contains(c, StringComparison.OrdinalIgnoreCase));
            bool heldOut = Hash(Key(artist)) % 10 < 3;
            var tempo = song.Chart.TempoMap.ToTempoMap();
            long? end = DifficultyNotes.EndTickOf(song.Chart);
            var parts = new List<Part>();
            foreach (var instrument in Instruments)
            {
                if (song.Chart.PartOf(instrument) is not { } editable)
                    continue;

                // Pro drums are rated twice: as 4-lane drums, and with their cymbals when the games read them as Pro.
                foreach (var (key, model) in DifficultyEstimator.KeyModelsOf(song.Chart, instrument, song.Options))
                {
                    if (!int.TryParse(song.Value(key)?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int reported) || reported < 0)
                        continue;

                    // As DifficultyEstimator.EstimatePart: the highest difficulty with two chords apart in time.
                    foreach (var difficulty in Enum.GetValues<ChartDifficulty>().Reverse())
                    {
                        if (editable[difficulty].Notes.Count == 0)
                            continue;
                        var track = GameView.Project(song.Chart, new ChartTrackKey(instrument, difficulty), song.Options);
                        var freeLanes = DifficultyNotes.FreeLanesOf(editable, difficulty);
                        var notes = DifficultyNotes.Of(track, tempo, end, DifficultyParameters.For(model), freeLanes);
                        if (notes.Count < 2 || notes[^1].TimeMs <= notes[0].TimeMs)
                            continue;
                        parts.Add(new Part(entry.FullPath, artist, title, charter, official, heldOut, instrument, key, model, difficulty, reported, track, tempo, end, freeLanes));
                        break;
                    }
                }
            }

            return parts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"  {entry.FullPath}: {ex.Message}");
            return null;
        }
    }

    public static string Key(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    // FNV-1a, so the split never changes with the runtime's string hashing.
    private static uint Hash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
            hash = (hash ^ c) * 16777619;
        return hash;
    }
}
