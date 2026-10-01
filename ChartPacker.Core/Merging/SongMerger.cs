using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Ini;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Text;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Merging;

/// <summary>What a merged song takes from each of its sources.</summary>
/// <param name="Analysis">The sources.</param>
/// <param name="BaseSource">The source whose chart gives the tempo map, events and delay.</param>
/// <param name="Parts">Each part to keep, by <see cref="MidiPart.Id"/>, and the source to take it from. Parts not listed are left out.</param>
/// <param name="Metadata">
/// For each of <see cref="SongMergeAnalysis.Conflicts"/>, the source whose value to keep, or
/// <see langword="null"/> to leave the key out. A conflict not listed keeps the base source's value.
/// </param>
/// <param name="AudioSource">The source whose audio files are kept, all of them.</param>
/// <param name="AlbumArtSource">The source whose album art is kept; <see langword="null"/> for none.</param>
/// <param name="BackgroundSource">The source whose background image is kept; <see langword="null"/> for none.</param>
/// <param name="VideoSource">The source whose background video is kept; <see langword="null"/> for none.</param>
/// <param name="ChartOffsets">
/// How far to move each instrument difficulty of the merged chart, in milliseconds, later when
/// positive, as the note highway sets them; see <see cref="ChartShifter"/>. Applied by
/// <see cref="SongMerger.MergeAsync"/>, not by <see cref="SongMerger.StageAsync"/>, whose preview
/// shows them as a draft's offsets instead.
/// </param>
/// <param name="AudioOffset">
/// How far to move the audio against the merged chart, in milliseconds, later when positive, as the
/// preview's audio offset sets it: worked into the <c>delay</c>; see <see cref="TimingOffsets"/>.
/// Applied, like <paramref name="ChartOffsets"/>, by <see cref="SongMerger.MergeAsync"/> and not by <see cref="SongMerger.StageAsync"/>.
/// </param>
/// <param name="VideoOffset">
/// How far to move the background video against the audio, in milliseconds, later when positive: worked
/// into the <c>video_start_time</c>. Applied as <paramref name="AudioOffset"/> is, and only with a video.
/// </param>
public sealed record SongMergePlan(
    SongMergeAnalysis Analysis,
    int BaseSource,
    IReadOnlyDictionary<string, int> Parts,
    IReadOnlyDictionary<string, int?> Metadata,
    int AudioSource,
    int? AlbumArtSource,
    int? BackgroundSource,
    int? VideoSource,
    IReadOnlyDictionary<ChartTrackKey, int>? ChartOffsets = null,
    int AudioOffset = 0,
    int VideoOffset = 0);

/// <summary>A merged song.</summary>
/// <param name="OutputPath">The <c>.sng</c> written.</param>
/// <param name="Warnings">Parts that didn't come out exactly as their source has them.</param>
/// <param name="PackFindings">Warnings from packing the <c>.sng</c>.</param>
public sealed record SongMergeResult(string OutputPath, IReadOnlyList<ChartMergeWarning> Warnings, IReadOnlyList<Finding> PackFindings);

/// <summary>Merges several songs into a new <c>.sng</c>, as a <see cref="SongMergePlan"/> says.</summary>
/// <remarks>
/// The song is put together in a temporary folder (a <c>notes.mid</c> from <see cref="MidiMerger"/>,
/// the chosen audio and images, and a <c>song.ini</c>), its <c>song_length</c> worked out by
/// <see cref="SongLengthEstimator"/>, and the folder packed with <see cref="PackPlanner"/> and
/// <see cref="Packer"/>. The folder is always deleted afterwards. No source is changed.
/// </remarks>
public static class SongMerger
{
    private const string SngExtension = ".sng";

    /// <summary>The merged song's metadata, <c>song_length</c> aside, in the base source's order with other keys after.</summary>
    /// <exception cref="ArgumentException">The plan names a source or part that doesn't exist.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildMetadata(SongMergePlan plan)
    {
        Validate(plan);
        var sources = plan.Analysis.Sources;
        var baseSource = sources[plan.BaseSource];
        var conflicts = plan.Analysis.Conflicts.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Skip(plan.BaseSource).Take(1).Concat(sources.Where((_, i) => i != plan.BaseSource)))
        {
            foreach (var (key, _) in source.Pairs)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        foreach (string key in (string[])["charter", "pro_drums", "delay"])
        {
            if (seen.Add(key))
                keys.Add(key);
        }

        string? From(int? index, string key) => index is { } i ? sources[i].Value(key) : null;

        string? ValueOf(string key)
        {
            switch (SongMergeAnalysis.RuleOf(key))
            {
                case MergeKeyRule.Charter:
                    string charters = CharterList.Merge(sources.Select(s => s.Value("charter")));
                    return charters.Length > 0 ? charters : null;

                case MergeKeyRule.Part:
                    var owner = MidiPartCatalog.OwnerOf(key)!;
                    if (!plan.Parts.TryGetValue(owner.Id, out int partSource))
                        return null;

                    // A converted .chart's cymbals only read as cymbals with pro_drums set.
                    if (key.Equals("pro_drums", StringComparison.OrdinalIgnoreCase) && sources[partSource].Chart?.Options.ProDrums == true)
                        return sources[partSource].Value(key) ?? "True";
                    return sources[partSource].Value(key);

                case MergeKeyRule.Base:
                    return baseSource.Value(key);

                case MergeKeyRule.Delay:
                    if (baseSource.Value(key) is { } delay)
                        return delay;
                    return baseSource.Chart is { Delay: var offset } && offset != TimeSpan.Zero
                        ? Math.Round(offset.TotalMilliseconds, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                        : null;

                case MergeKeyRule.Audio:
                    return From(plan.AudioSource, key);
                case MergeKeyRule.AlbumArt:
                    return From(plan.AlbumArtSource, key);
                case MergeKeyRule.Background:
                    return From(plan.BackgroundSource, key);
                case MergeKeyRule.Video:
                    return From(plan.VideoSource, key);
                case MergeKeyRule.SongLength:
                    return null;
                default:
                    if (!conflicts.ContainsKey(key))
                        return sources.Select(s => s.Value(key)).FirstOrDefault(v => v is not null);
                    return plan.Metadata.TryGetValue(key, out int? chosen) ? From(chosen, key) : baseSource.Value(key);
            }
        }

        return keys
            .Select(k => (Key: k, Value: ValueOf(k)))
            .Where(p => p.Value is not null)
            .Select(p => KeyValuePair.Create(p.Key, p.Value!))
            .ToList();
    }

    /// <summary>
    /// The file name, without <c>.sng</c>, for a song with <paramref name="pairs"/>: <c>Artist - Name</c>
    /// without rich-text tags or characters a file name can't have, or <paramref name="fallback"/>.
    /// </summary>
    public static string FileNameFor(IReadOnlyList<KeyValuePair<string, string>> pairs, string fallback)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        var headline = SongHeadline.From(pairs);
        string name = string.Join(" - ", new[] { headline.Artist, headline.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        string clean = new string([.. RichText.ToPlainText(name).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.').Trim();
        return clean.Length > 0 ? clean : fallback;
    }

    /// <summary>
    /// Where the merged song goes in <paramref name="outputFolder"/> under <paramref name="policy"/>:
    /// <see langword="null"/> when the file exists and is to be skipped.
    /// </summary>
    public static string? TargetFor(SongMergePlan plan, string outputFolder, OverwritePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        string name = FileNameFor(BuildMetadata(plan), plan.Analysis.Sources[plan.BaseSource].Entry.DisplayName);
        return OutputPaths.Resolve(Path.Combine(Path.GetFullPath(outputFolder), name + SngExtension), policy, isDirectory: false);
    }

    /// <summary>Whether <paramref name="path"/> is one of the plan's sources, which a merge must never write over.</summary>
    public static bool IsSource(SongMergePlan plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return plan.Analysis.Sources.Any(s => OutputPaths.PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.Entry.FullPath)), full));
    }

    /// <summary>Merges the plan's songs into a new <c>.sng</c> in <paramref name="outputFolder"/>.</summary>
    /// <param name="plan">What to take from each song.</param>
    /// <param name="outputFolder">The folder the <c>.sng</c> goes in.</param>
    /// <param name="policy">What to do when the <c>.sng</c> exists.</param>
    /// <param name="video">Reads the background video's length, for <c>song_length</c>.</param>
    /// <param name="cancellationToken">Cancels the merge; nothing is left behind.</param>
    /// <exception cref="ArgumentException">The plan names a source or part that doesn't exist, or its base has no chart.</exception>
    /// <exception cref="IOException">
    /// The <c>.sng</c> exists and <paramref name="policy"/> is to skip, it would replace a source, a chart
    /// can't be merged (<see cref="ChartFormatException"/>), or a file can't be read or written.
    /// </exception>
    /// <exception cref="InvalidOperationException">The merged song can't be packed; the message lists why.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The merge was cancelled.</exception>
    public static Task<SongMergeResult> MergeAsync(
        SongMergePlan plan,
        string outputFolder,
        OverwritePolicy policy,
        IVideoDurationProbe video,
        CancellationToken cancellationToken = default)
    {
        Validate(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(video);
        return Task.Run(() => MergeCoreAsync(plan, outputFolder, policy, video, cancellationToken), cancellationToken);
    }

    private static async Task<SongMergeResult> MergeCoreAsync(SongMergePlan plan, string outputFolder, OverwritePolicy policy, IVideoDurationProbe video, CancellationToken cancellationToken)
    {
        var sources = plan.Analysis.Sources;
        string target = TargetFor(plan, outputFolder, policy)
            ?? throw new IOException($"'{Path.Combine(outputFolder, FileNameFor(BuildMetadata(plan), sources[plan.BaseSource].Entry.DisplayName) + SngExtension)}' already exists.");
        if (IsSource(plan, target))
            throw new IOException($"'{target}' is one of the songs being merged, so it can't be written over.");

        var staging = Directory.CreateTempSubdirectory("chartpacker-merge-");
        try
        {
            var warnings = await StageCoreAsync(plan, staging.FullName, video, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), applyOffsets: true, cancellationToken).ConfigureAwait(false);

            var packPlan = await PackPlanner.PlanAsync(staging.FullName, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (packPlan.HasErrors)
                throw new InvalidOperationException(string.Join(Environment.NewLine, packPlan.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));

            var findings = await Packer.PackAsync(packPlan, target, overwrite: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (findings.Any(f => f.Severity == Severity.Error))
                throw new InvalidOperationException(string.Join(Environment.NewLine, findings.Where(f => f.Severity == Severity.Error).Select(f => f.Message)));

            return new SongMergeResult(target, warnings, [.. findings.Concat(packPlan.Findings).Where(f => f.Severity == Severity.Warning)]);
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A temporary folder that can't be deleted now is the system's to clean up.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Puts the merged song together as a song folder in <paramref name="folder"/>, as it would be
    /// packed: its <c>notes.mid</c>, the chosen audio and images, and a <c>song.ini</c> with its
    /// <c>song_length</c> worked out. Used to preview a merge.
    /// </summary>
    /// <param name="plan">What to take from each song.</param>
    /// <param name="folder">An existing folder that belongs to the caller. Files in it the song doesn't have are deleted.</param>
    /// <param name="video">Reads the background video's length, for <c>song_length</c>.</param>
    /// <param name="staged">
    /// Where each file already in <paramref name="folder"/> came from, by name, as a previous call left it;
    /// a file that would come from the same place again isn't copied again. Updated by the call.
    /// Pass an empty dictionary for a new folder.
    /// </param>
    /// <param name="cancellationToken">Cancels staging, which may leave the folder half staged.</param>
    /// <returns>Parts that didn't come out exactly as their source has them.</returns>
    /// <exception cref="ArgumentException">The plan names a source or part that doesn't exist, or its base has no chart.</exception>
    /// <exception cref="IOException">A chart can't be merged (<see cref="ChartFormatException"/>), or a file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Staging was cancelled.</exception>
    public static Task<IReadOnlyList<ChartMergeWarning>> StageAsync(
        SongMergePlan plan,
        string folder,
        IVideoDurationProbe video,
        IDictionary<string, string> staged,
        CancellationToken cancellationToken = default)
    {
        Validate(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(staged);
        return Task.Run(() => StageCoreAsync(plan, folder, video, staged, applyOffsets: false, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<ChartMergeWarning>> StageCoreAsync(
        SongMergePlan plan, string folder, IVideoDurationProbe video, IDictionary<string, string> staged, bool applyOffsets, CancellationToken cancellationToken)
    {
        var sources = plan.Analysis.Sources;
        var baseChart = sources[plan.BaseSource].Chart
            ?? throw new ArgumentException("The base song has no chart that can be read.", nameof(plan));

        var parts = plan.Parts
            .Select(p => (Source: sources[p.Value].Chart!, Part: plan.Analysis.Parts.First(part => part.Id == p.Key)))
            .ToList();
        var merged = MidiMerger.Merge(baseChart, parts);
        var warnings = plan.Parts.Values.Distinct().SelectMany(i => sources[i].ConversionWarnings.Where(w => parts.Any(p => ReferenceEquals(p.Source, sources[i].Chart) && p.Part.Has(w.TrackName))))
            .Concat(merged.Warnings)
            .ToList();
        var pairs = BuildMetadata(plan).ToList();
        if (applyOffsets)
            pairs = WithTimingOffsets(pairs, plan);

        byte[] midi = merged.Midi;
        if (applyOffsets && plan.ChartOffsets is { Count: > 0 } offsets)
        {
            // Read as the merged song.ini says, pro_drums included, as the highway that set them read it.
            var shifted = ChartShifter.Shift(midi, ChartFiles.MidiName, offsets, ChartReadOptions.From(pairs));
            midi = shifted.Bytes;
            warnings.AddRange(shifted.ChangedMarkings.Select(k => new ChartMergeWarning(ChartMergeIssue.MarkingsChanged, ChartToMidi.TrackNames[k.Instrument], k.Difficulty)));
        }

        // The files the song takes, by name, and the song each comes from.
        var wanted = new Dictionary<string, MergeSource>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in sources[plan.AudioSource].AudioFiles)
            wanted[name] = sources[plan.AudioSource];
        if (plan.AlbumArtSource is { } art && sources[art].AlbumArt is { } artName)
            wanted[artName] = sources[art];
        if (plan.BackgroundSource is { } background && sources[background].BackgroundImage is { } backgroundName)
            wanted[backgroundName] = sources[background];
        if (plan.VideoSource is { } videoSource && sources[videoSource].Video is { } videoName)
            wanted[videoName] = sources[videoSource];

        // What's there from an earlier call stays when it would come from the same place; anything else goes.
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(path);
            bool keep = wanted.TryGetValue(name, out var from) && staged.TryGetValue(name, out string? was) && was == KeyOf(from, name);
            if (!keep)
            {
                File.Delete(path);
                staged.Remove(name);
            }
        }

        foreach (string name in staged.Keys.Where(n => !File.Exists(Path.Combine(folder, n))).ToList())
            staged.Remove(name);

        foreach (var (name, from) in wanted)
        {
            if (staged.ContainsKey(name))
                continue;
            await CopyAsync(from, name, folder, cancellationToken).ConfigureAwait(false);
            staged[name] = KeyOf(from, name);
        }

        await File.WriteAllBytesAsync(Path.Combine(folder, ChartFiles.MidiName), midi, cancellationToken).ConfigureAwait(false);

        var entry = new SongEntry(SongEntryKind.SongFolder, folder, ".", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)));
        if (SongLengthEstimator.Estimate(entry, new MetadataDraft(), pairs, video, cancellationToken).SongLengthMs is { } length)
            SetLength(pairs, length);
        await SongIni.WriteAsync(Path.Combine(folder, SongIni.FileName), pairs, cancellationToken).ConfigureAwait(false);
        return warnings;
    }

    /// <summary>
    /// <paramref name="pairs"/> with the plan's audio and video offsets worked into their keys. The merged chart is a
    /// <c>notes.mid</c>, which has no offset of its own, so an unset <c>delay</c> is zero.
    /// </summary>
    private static List<KeyValuePair<string, string>> WithTimingOffsets(List<KeyValuePair<string, string>> pairs, SongMergePlan plan)
    {
        var draft = new MetadataDraft();
        TimingOffsets.Set(draft, TimingOffsets.AudioKey, pairs, TimeSpan.Zero, plan.AudioOffset);
        if (plan.VideoSource is not null)
            TimingOffsets.Set(draft, TimingOffsets.VideoKey, pairs, TimeSpan.Zero, plan.VideoOffset);
        return draft.ApplyTo(pairs);
    }

    private static string KeyOf(MergeSource source, string storedName) => source.Entry.FullPath + "|" + storedName;

    private static void SetLength(List<KeyValuePair<string, string>> pairs, long lengthMs)
    {
        var pair = KeyValuePair.Create("song_length", lengthMs.ToString(CultureInfo.InvariantCulture));
        int at = pairs.FindIndex(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            pairs[at] = pair;
        else
            pairs.Insert(Math.Min(pairs.Count, pairs.FindIndex(p => p.Key.Equals("charter", StringComparison.OrdinalIgnoreCase)) + 1), pair);
    }

    private static async Task CopyAsync(MergeSource source, string storedName, string folder, CancellationToken cancellationToken)
    {
        await using var input = SongFile.OpenRead(source.Entry, storedName, cancellationToken)
            ?? throw new FileNotFoundException($"'{storedName}' is missing from '{source.Entry.FullPath}'.");
        await using var output = new FileStream(Path.Combine(folder, storedName), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(SongMergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sources = plan.Analysis.Sources;
        bool InRange(int? i) => i is null || (i >= 0 && i < sources.Count);

        if (!InRange(plan.BaseSource) || !InRange(plan.AudioSource) || !InRange(plan.AlbumArtSource) || !InRange(plan.BackgroundSource) || !InRange(plan.VideoSource)
            || plan.Metadata.Values.Any(i => !InRange(i)))
            throw new ArgumentException("The plan names a song that isn't one of its sources.", nameof(plan));

        foreach (var (partId, source) in plan.Parts)
        {
            if (!InRange(source) || !sources[source].HasPart(partId))
                throw new ArgumentException($"The song chosen for '{partId}' doesn't have it.", nameof(plan));
        }
    }
}
