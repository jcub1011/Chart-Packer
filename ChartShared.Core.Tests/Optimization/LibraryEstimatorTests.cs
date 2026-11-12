using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// A library's savings measured a chart at a time: the per-type sample (<see cref="SampleSelector"/>), what's predicted
/// from it (<see cref="SavingsModel"/>), the estimates kept between plans (<see cref="EstimateCache"/>), and
/// <see cref="LibraryEstimator"/> that puts them together. Synthetic songs only.
/// </summary>
[TestClass]
public sealed class LibraryEstimatorTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-optest-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static OptimizerSet SetOf(FakeOptimizer optimizer) => new(optimizer, optimizer, optimizer);

    /// <summary>Audio, video and both images on; no metadata cleanup, which reads the chart.</summary>
    private static OptimizeOptions Media(double margin = 0.1)
    {
        var all = AllOn(margin);
        return all with { Metadata = all.Metadata with { Enabled = false } };
    }

    /// <summary>A chart that's only described, at <paramref name="relativePath"/> under <paramref name="root"/>.</summary>
    private static ChartAssets Described(string relativePath, string root, params ChartAsset[] assets) =>
        new(new SongEntry(SongEntryKind.SongFolder, Path.Combine(root, relativePath), relativePath, relativePath), assets);

    /// <summary>Forty described charts with a stem and album art; three also have a video, and one a background image.</summary>
    private static List<ChartAssets> DescribedLibrary(string root = @"C:\Library")
    {
        var charts = new List<ChartAssets>();
        for (int i = 0; i < 40; i++)
        {
            var assets = new List<ChartAsset> { Asset("guitar.ogg", 1000 + i), Asset("album.jpg", 500) };
            if (i is 7 or 19 or 33)
                assets.Add(Asset("video.mp4", 9000));
            if (i == 12)
                assets.Add(Asset("background.png", 4000));
            charts.Add(Described($"Song {i:D2}", root, [.. assets]));
        }

        return charts;
    }

    private async Task<ChartAssets> OnDiskAsync(string name, params (string Name, byte[] Data)[] files) =>
        await AssetInspector.InspectAsync(Folder(_root.FullName, name, null, files), cancellationToken: TestContext.CancellationToken);

    private async Task<List<ChartAssets>> OnDiskLibraryAsync(int count, Func<int, bool>? hasVideo = null)
    {
        var charts = new List<ChartAssets>();
        for (int i = 0; i < count; i++)
        {
            var files = new List<(string, byte[])> { ("guitar.ogg", Bytes(1000 + (i * 10), 1)) };
            if (hasVideo?.Invoke(i) == true)
                files.Add(("video.mp4", Bytes(4000, 3)));
            charts.Add(await OnDiskAsync($"Song {i:D2}", [.. files]));
        }

        return charts;
    }

    // ---- The sample --------------------------------------------------------------------------------------------

    [TestMethod]
    public void EveryTypeIsMeasuredToItsQuotaOrInFull()
    {
        var library = DescribedLibrary();
        var options = Media();

        var sample = SampleSelector.Cover(library, options, quotaPerType: 5);

        var measured = sample.SelectMany(c => OptimizationPlanner.UnitsOf(c, options)).GroupBy(u => u.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.IsGreaterThanOrEqualTo(5, measured[new MediaType(OptimizeStrategy.Audio, "audio/ogg")]);
        Assert.IsGreaterThanOrEqualTo(5, measured[new MediaType(OptimizeStrategy.AlbumArt, "image/jpeg")]);
        // Types the library has fewer than the quota of are measured in full, however few charts hold them.
        Assert.AreEqual(3, measured[new MediaType(OptimizeStrategy.Video, "video/mp4")]);
        Assert.AreEqual(1, measured[new MediaType(OptimizeStrategy.BackgroundArt, "image/png")]);
        // The charts chosen for the rare types count towards the common ones, so few more are needed.
        Assert.HasCount(5, sample);
    }

    [TestMethod]
    public void EveryTypeHasAChartMeasuredBeforeAnyHasItsQuota()
    {
        var library = DescribedLibrary();
        var options = Media();

        var sample = SampleSelector.Cover(library, options, quotaPerType: 5);

        // The background image and the first video come in the first round, not after every video.
        var first = sample.Take(2).SelectMany(c => OptimizationPlanner.UnitsOf(c, options)).Select(u => u.Type).ToHashSet();
        Assert.Contains(new MediaType(OptimizeStrategy.BackgroundArt, "image/png"), first);
        Assert.Contains(new MediaType(OptimizeStrategy.Video, "video/mp4"), first);
        Assert.Contains(new MediaType(OptimizeStrategy.Audio, "audio/ogg"), first);
    }

    [TestMethod]
    public void TheSampleIsTheSameHoweverTheLibraryIsOrderedAndWhereverItIs()
    {
        var options = Media();
        var first = SampleSelector.Cover(DescribedLibrary(), options, 5).Select(c => c.Entry.RelativePath).ToList();

        var moved = DescribedLibrary(@"D:\Elsewhere\Songs");
        moved.Reverse();
        var second = SampleSelector.Cover(moved, options, 5).Select(c => c.Entry.RelativePath).ToList();

        Assert.AreSequenceEqual(first, second);
    }

    [TestMethod]
    public void WhatsKnownWithoutMeasuringIsntSampled()
    {
        var library = new List<ChartAssets>
        {
            Described("Opus", @"C:\Library", Asset("guitar.opus", 1000)),
            Described("Removed", @"C:\Library", Asset("video.mp4", 9000)),
        };
        var options = Media() with { Video = new VideoOptimizeOptions(Enabled: true, Remove: true) };

        Assert.HasCount(0, SampleSelector.Cover(library, options));
        Assert.AreEqual(0, OptimizationPlanner.UnitsOf(library[0], options).Single().KnownSavingBytes);
        Assert.AreEqual(9000, OptimizationPlanner.UnitsOf(library[1], options).Single().KnownSavingBytes);
    }

    [TestMethod]
    public void AQuotaBelowOneIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SampleSelector.Cover([], Media(), 0));

    // ---- Predictions -------------------------------------------------------------------------------------------

    private static ChartPlan PlanOf(ChartAssets chart, params PlannedAction[] actions) => new(chart, actions);

    private static PlannedAction Transcode(string name, long input, long output) =>
        new(OptimizeStrategy.Audio, OptimizeActionKind.Transcode, [name], Path.ChangeExtension(name, ".opus"), "audio/ogg", input, output);

    private static PlannedAction Skip(string name, long input, long output, OptimizeSkipReason reason) =>
        new(OptimizeStrategy.Audio, OptimizeActionKind.Skip, [name], null, "audio/ogg", input, output, reason);

    [TestMethod]
    public void APredictionIsTheTypesMeasuredShareWithSkipsSavingNothing()
    {
        var options = Only(OptimizeStrategy.Audio);
        var model = new SavingsModel(options);
        var applied = Described("A", @"C:\Library", Asset("guitar.ogg", 1000));
        var skipped = Described("B", @"C:\Library", Asset("guitar.ogg", 1000));
        model.Add(PlanOf(applied, Transcode("guitar.ogg", 1000, 400)));
        // Below the margin, so the run leaves it: its 50 bytes are never saved.
        model.Add(PlanOf(skipped, Skip("guitar.ogg", 1000, 950, OptimizeSkipReason.BelowMargin)));

        var type = new MediaType(OptimizeStrategy.Audio, "audio/ogg");
        Assert.AreEqual(2, model.MeasuredFiles(type));
        Assert.AreEqual(0.3, model.RatioOf(type));

        var unmeasured = Described("C", @"C:\Library", Asset("guitar.ogg", 2000), Asset("rhythm.ogg", 1000));
        var predicted = model.Predict(OptimizationPlanner.UnitsOf(unmeasured, options));
        Assert.AreEqual(900, predicted.TotalBytes);
        Assert.AreEqual(900, predicted[OptimizeStrategy.Audio]);
        Assert.IsTrue(predicted.IsComplete);
    }

    [TestMethod]
    public void ATypeNothingIsKnownAboutIsUnknownNotZero()
    {
        var options = Media();
        var model = new SavingsModel(options);
        model.Add(PlanOf(Described("A", @"C:\Library", Asset("guitar.ogg", 1000)), Transcode("guitar.ogg", 1000, 500)));

        var chart = Described("B", @"C:\Library", Asset("guitar.ogg", 1000), Asset("video.mp4", 9000));
        var predicted = model.Predict(OptimizationPlanner.UnitsOf(chart, options));

        Assert.IsFalse(predicted.IsComplete, "nothing has been measured of video/mp4");
        Assert.AreEqual(500, predicted.TotalBytes);
        Assert.IsNull(model.RatioOf(new MediaType(OptimizeStrategy.Video, "video/mp4")));
    }

    [TestMethod]
    public void AFileThatCouldntBeMeasuredSaysNothingAboutItsType()
    {
        var options = Only(OptimizeStrategy.Audio);
        var model = new SavingsModel(options);
        model.Add(PlanOf(Described("A", @"C:\Library", Asset("guitar.ogg", 1000)), Skip("guitar.ogg", 1000, 1000, OptimizeSkipReason.EstimateFailed)));

        Assert.AreEqual(0, model.MeasuredFiles(new MediaType(OptimizeStrategy.Audio, "audio/ogg")));
    }

    [TestMethod]
    public void AStemTheMixdownTookCountsAsAudioThatSavedNothing()
    {
        var all = AllOn();
        var options = Only(OptimizeStrategy.Audio) with { StemMixdown = all.StemMixdown with { Enabled = true } };
        var model = new SavingsModel(options);
        var chart = Described("A", @"C:\Library", Asset("guitar.ogg", 1000), Asset("rhythm.ogg", 1000));
        model.Add(PlanOf(chart, new PlannedAction(
            OptimizeStrategy.StemMixdown, OptimizeActionKind.MixDown, ["guitar.ogg", "rhythm.ogg"], "song.opus", "audio/ogg", 2000, 500)));

        // The same stems are units of both strategies; only the mixdown saved anything, so only it predicts a saving.
        var twin = Described("B", @"C:\Library", Asset("guitar.ogg", 1000), Asset("rhythm.ogg", 1000));
        var predicted = model.Predict(OptimizationPlanner.UnitsOf(twin, options));

        Assert.AreEqual(1500, predicted.TotalBytes);
        Assert.AreEqual(1500, predicted[OptimizeStrategy.StemMixdown]);
        Assert.AreEqual(0, predicted[OptimizeStrategy.Audio]);
    }

    [TestMethod]
    public async Task AMeasuredChartsSavingIsItsPlansWork()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)), ("rhythm.ogg", Bytes(1000, 2)), ("video.mp4", Bytes(4000, 3)));
        var optimizer = new FakeOptimizer
        {
            // The video would only save 5%, under the margin: it's skipped, and saves nothing.
            Estimate = files => FakeOptimizer.DefaultEstimate(files[0].Asset.Kind == AssetKind.Video ? 0.95 : 0.5)(files),
        };

        var plan = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), TestContext.CancellationToken);
        var savings = SavingsModel.Measured(plan);

        Assert.AreEqual(1000, savings.TotalBytes);
        Assert.AreEqual(1000, savings[OptimizeStrategy.Audio]);
        Assert.AreEqual(0, savings[OptimizeStrategy.Video]);
        Assert.IsTrue(savings.IsComplete);
    }

    // ---- The cache ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ASecondPlanMeasuresNothingAndIsTheSamePlan()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)), ("video.mp4", Bytes(4000, 3)), ("album.jpg", Bytes(500, 5)));
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());

        var first = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), services, TestContext.CancellationToken);
        Assert.HasCount(3, optimizer.Estimated);
        var second = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), services, TestContext.CancellationToken);

        Assert.HasCount(3, optimizer.Estimated, "the second plan asked the optimizer nothing");
        Assert.AreSequenceEqual(
            first.Actions.Select(a => (a.Strategy, a.Kind, a.OutputName, a.InputBytes, a.EstimatedOutputBytes, a.Reason)).ToList(),
            second.Actions.Select(a => (a.Strategy, a.Kind, a.OutputName, a.InputBytes, a.EstimatedOutputBytes, a.Reason)).ToList());
    }

    [TestMethod]
    public async Task ASngIsntExtractedForAPlanTheCacheAnswers()
    {
        var entry = await SngAsync(_root.FullName, "Packed", null, ("guitar.ogg", Bytes(1000, 1)));
        var chart = await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken);
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());
        await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), services, TestContext.CancellationToken);

        // With the .sng held shut, a plan that had to extract it would fail its estimate.
        using (new FileStream(entry.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), services, TestContext.CancellationToken);
            Assert.AreEqual(OptimizeActionKind.Transcode, ActionFor(plan, OptimizeStrategy.Audio).Kind);
        }

        Assert.HasCount(1, optimizer.Estimated);
    }

    [TestMethod]
    public async Task AChangedSettingMeasuresOnlyItsOwnStrategy()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)), ("video.mp4", Bytes(4000, 3)), ("album.jpg", Bytes(500, 5)));
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());
        var options = Media();
        await OptimizationPlanner.PlanAsync(chart, options, SetOf(optimizer), services, TestContext.CancellationToken);
        optimizer.Estimated.Clear();

        await OptimizationPlanner.PlanAsync(chart, options with { Audio = options.Audio with { BitrateKbps = 80 } }, SetOf(optimizer), services, TestContext.CancellationToken);

        Assert.AreSequenceEqual(["guitar.ogg"], optimizer.Estimated);
    }

    [TestMethod]
    public async Task AChangedMarginJudgesAgainWithoutMeasuring()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)));
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());
        var loose = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio, margin: 0.1), SetOf(optimizer), services, TestContext.CancellationToken);

        var strict = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio, margin: 0.6), SetOf(optimizer), services, TestContext.CancellationToken);

        Assert.AreEqual(OptimizeActionKind.Transcode, ActionFor(loose, OptimizeStrategy.Audio).Kind);
        Assert.AreEqual(OptimizeSkipReason.BelowMargin, ActionFor(strict, OptimizeStrategy.Audio).Reason);
        Assert.HasCount(1, optimizer.Estimated);
    }

    [TestMethod]
    public async Task AChartThatChangedIsMeasuredAgain()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)));
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());
        await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio), SetOf(optimizer), services, TestContext.CancellationToken);

        File.WriteAllBytes(Path.Combine(chart.Entry.FullPath, "guitar.ogg"), Bytes(1400, 2));
        var changed = await AssetInspector.InspectAsync(chart.Entry, cancellationToken: TestContext.CancellationToken);
        var plan = await OptimizationPlanner.PlanAsync(changed, Only(OptimizeStrategy.Audio), SetOf(optimizer), services, TestContext.CancellationToken);

        Assert.HasCount(2, optimizer.Estimated);
        Assert.AreEqual(700, ActionFor(plan, OptimizeStrategy.Audio).EstimatedOutputBytes);
    }

    [TestMethod]
    public async Task AFailedEstimateIsntKept()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)));
        var optimizer = new FakeOptimizer { FailEstimateWhen = _ => true };
        var services = new PlanServices(new EstimateCache());
        var failed = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio), SetOf(optimizer), services, TestContext.CancellationToken);
        Assert.AreEqual(OptimizeSkipReason.EstimateFailed, ActionFor(failed, OptimizeStrategy.Audio).Reason);

        optimizer.FailEstimateWhen = null;
        var plan = await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio), SetOf(optimizer), services, TestContext.CancellationToken);

        Assert.AreEqual(OptimizeActionKind.Transcode, ActionFor(plan, OptimizeStrategy.Audio).Kind);
    }

    [TestMethod]
    public async Task AChartThatWasntStampedIsNeverCached()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1))) with { Stamp = null };
        var optimizer = new FakeOptimizer();
        var cache = new EstimateCache();

        await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio), SetOf(optimizer), new PlanServices(cache), TestContext.CancellationToken);
        await OptimizationPlanner.PlanAsync(chart, Only(OptimizeStrategy.Audio), SetOf(optimizer), new PlanServices(cache), TestContext.CancellationToken);

        Assert.AreEqual(0, cache.Count);
        Assert.HasCount(2, optimizer.Estimated);
    }

    [TestMethod]
    public async Task ASavedCacheAnswersTheNextSession()
    {
        string path = Path.Combine(_root.FullName, "optimizer-estimates.json");
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)), ("video.mp4", Bytes(4000, 3)));
        var optimizer = new FakeOptimizer();
        var cache = new EstimateCache(path);
        var first = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), new PlanServices(cache), TestContext.CancellationToken);
        Assert.IsTrue(cache.Save());
        optimizer.Estimated.Clear();

        var reopened = new EstimateCache(path);
        var second = await OptimizationPlanner.PlanAsync(chart, Media(), SetOf(optimizer), new PlanServices(reopened), TestContext.CancellationToken);

        Assert.AreEqual(2, reopened.Count);
        Assert.HasCount(0, optimizer.Estimated);
        Assert.AreSequenceEqual(first.Actions.Select(a => a.EstimatedOutputBytes).ToList(), second.Actions.Select(a => a.EstimatedOutputBytes).ToList());
        Assert.AreSequenceEqual(first.Actions.Select(a => a.OutputName).ToList(), second.Actions.Select(a => a.OutputName).ToList());
    }

    [TestMethod]
    public void AnUnreadableCacheFileIsAnEmptyCache()
    {
        string path = Path.Combine(_root.FullName, "optimizer-estimates.json");
        File.WriteAllText(path, "{ not json");

        Assert.AreEqual(0, new EstimateCache(path).Count);
    }

    [TestMethod]
    public void ACacheWithNoFileSavesNothing() => Assert.IsFalse(new EstimateCache().Save());

    // ---- The estimator -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task EveryChartIsMeasuredTheSampleFirst()
    {
        var charts = await OnDiskLibraryAsync(12, i => i is 4 or 9);
        var optimizer = new FakeOptimizer();
        var estimator = new LibraryEstimator(charts, Media(), SetOf(optimizer), quotaPerType: 2);
        var progress = new SyncProgress<ChartEstimate>();

        await estimator.RunAsync(progress, TestContext.CancellationToken);

        Assert.HasCount(12, progress.Reports);
        Assert.AreSequenceEqual(Enumerable.Range(1, 12).ToList(), progress.Reports.Select(r => r.Done).ToList());
        Assert.IsTrue(progress.Reports.All(r => r.Total == 12 && r.Plan is not null && r.Error is null));
        Assert.HasCount(12, progress.Reports.Select(r => r.Chart.Entry.FullPath).Distinct());

        // The two charts with a video are the whole of that type, and bring the audio quota with them.
        Assert.HasCount(2, estimator.Sample);
        Assert.AreSequenceEqual(estimator.Sample.Select(c => c.Entry.FullPath).ToList(), progress.Reports.Take(2).Select(r => r.Chart.Entry.FullPath).ToList());
        Assert.IsFalse(progress.Reports[0].SampleComplete);
        Assert.IsTrue(progress.Reports.Skip(1).All(r => r.SampleComplete));

        // After the sample, the charts with the most to measure come first.
        var rest = progress.Reports.Skip(2).Select(r => r.Chart.TotalBytes).ToList();
        Assert.AreSequenceEqual(rest.OrderByDescending(b => b).ToList(), rest);
        Assert.AreEqual(12, estimator.Model.MeasuredFiles(new MediaType(OptimizeStrategy.Audio, "audio/ogg")));
    }

    [TestMethod]
    public async Task APrioritizedChartIsMeasuredNext()
    {
        var charts = await OnDiskLibraryAsync(6);
        var estimator = new LibraryEstimator(charts, Media(), SetOf(new FakeOptimizer()), quotaPerType: 2);
        var wanted = charts.Single(c => !estimator.Sample.Contains(c) && c.Entry.DisplayName == charts.Where(x => !estimator.Sample.Contains(x)).Min(x => x.Entry.DisplayName));
        estimator.Prioritize(wanted.Entry);
        var progress = new SyncProgress<ChartEstimate>();

        await estimator.RunAsync(progress, TestContext.CancellationToken);

        Assert.AreEqual(wanted.Entry.FullPath, progress.Reports[0].Chart.Entry.FullPath);
        Assert.HasCount(6, progress.Reports);
    }

    [TestMethod]
    public async Task MeasuringWaitsWhilePaused()
    {
        var charts = await OnDiskLibraryAsync(3);
        var estimator = new LibraryEstimator(charts, Media(), SetOf(new FakeOptimizer()));
        var progress = new SyncProgress<ChartEstimate>();
        estimator.Pause();
        Assert.IsTrue(estimator.IsPaused);

        var run = estimator.RunAsync(progress, TestContext.CancellationToken);
        await Task.Delay(150, TestContext.CancellationToken);
        Assert.IsFalse(run.IsCompleted);
        Assert.HasCount(0, progress.Reports);

        estimator.Resume();
        await run;

        Assert.IsFalse(estimator.IsPaused);
        Assert.HasCount(3, progress.Reports);
    }

    [TestMethod]
    public async Task ACancelStopsMeasuringEvenWhilePaused()
    {
        var charts = await OnDiskLibraryAsync(3);
        var estimator = new LibraryEstimator(charts, Media(), SetOf(new FakeOptimizer()));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        estimator.Pause();
        var run = estimator.RunAsync(null, cancel.Token);

        cancel.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
    }

    [TestMethod]
    public async Task TheRunThatFollowsMeasuresNothing()
    {
        var charts = await OnDiskLibraryAsync(5, i => i == 2);
        var optimizer = new FakeOptimizer();
        var services = new PlanServices(new EstimateCache());
        var options = Media();
        await new LibraryEstimator(charts, options, SetOf(optimizer), services).RunAsync(null, TestContext.CancellationToken);
        int measured = optimizer.Estimated.Count;
        Assert.AreEqual(6, measured);

        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), services, null, TestContext.CancellationToken);
        var result = await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(measured, optimizer.Estimated.Count, "planning the run asked the optimizers nothing");
        Assert.AreEqual(5, result.Succeeded);
    }

    [TestMethod]
    public async Task OnlyAsManyVideosAsTheGateAdmitsAreMeasuredAtOnce()
    {
        var charts = await OnDiskLibraryAsync(6, _ => true);
        int current = 0, most = 0;
        var optimizer = new FakeOptimizer
        {
            Estimate = files =>
            {
                if (files[0].Asset.Kind == AssetKind.Video)
                {
                    int now = Interlocked.Increment(ref current);
                    int seen;
                    while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen)
                    {
                    }

                    Thread.Sleep(40);
                    Interlocked.Decrement(ref current);
                }

                return FakeOptimizer.DefaultEstimate(0.5)(files);
            },
        };
        using var gate = new SemaphoreSlim(1);

        await new LibraryEstimator(charts, Media(), SetOf(optimizer), new PlanServices(null, gate), parallelism: 4).RunAsync(null, TestContext.CancellationToken);

        Assert.AreEqual(1, most);
    }

    // ---- What the run reports ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task AResultCarriesWhatWasEstimatedBesideWhatItCameTo()
    {
        var chart = await OnDiskAsync("Song", ("guitar.ogg", Bytes(1000, 1)));
        var optimizer = new FakeOptimizer { WrittenLength = _ => 430 };
        var options = Only(OptimizeStrategy.Audio);
        var plan = await OptimizationPlanner.PlanAsync(chart, options, SetOf(optimizer), TestContext.CancellationToken);

        var result = await OptimizeJob.RunAsync([plan], options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        var asset = result.Items[0].Assets.Single(a => a.Strategy == OptimizeStrategy.Audio);
        Assert.AreEqual(AssetOutcome.Applied, asset.Outcome);
        Assert.AreEqual(500, asset.EstimatedBytesAfter);
        Assert.AreEqual(430, asset.BytesAfter);
    }

    // ---- Audio headers -----------------------------------------------------------------------------------------

    private static byte[] OggPage(string codecHeader)
    {
        byte[] payload = System.Text.Encoding.Latin1.GetBytes(codecHeader + "........");
        var page = new byte[27 + 1 + payload.Length];
        "OggS"u8.CopyTo(page);
        page[26] = 1;
        page[27] = (byte)payload.Length;
        payload.CopyTo(page, 28);
        return page;
    }

    [TestMethod]
    public async Task AnOggIsToldApartByWhatItHolds()
    {
        var probe = new AudioHeaderProbe();

        async Task<string?> CodecAsync(byte[] bytes) =>
            (await probe.ProbeAsync("guitar.ogg", new MemoryStream(bytes), TestContext.CancellationToken))?.CodecKey;

        Assert.AreEqual("audio/opus", await CodecAsync(OggPage("OpusHead")));
        Assert.AreEqual("audio/vorbis", await CodecAsync(OggPage("\u0001vorbis")));
        Assert.AreEqual("audio/flac", await CodecAsync(OggPage("\u007fFLAC")));
        Assert.IsNull(await CodecAsync(OggPage("Speex   ")));
        Assert.IsNull(await CodecAsync(Bytes(400, 3)), "not an Ogg stream: the extension stands");
        Assert.IsNull(await CodecAsync([]));
    }

    [TestMethod]
    public async Task OpusInAnOggIsKnownToBeLeftAlone()
    {
        var entry = Folder(_root.FullName, "Song", null, ("guitar.ogg", OggPage("OpusHead")), ("rhythm.ogg", OggPage("\u0001vorbis")));
        var chart = await AssetInspector.InspectAsync(entry, new AssetProbes(new AudioHeaderProbe()), TestContext.CancellationToken);

        var units = OptimizationPlanner.UnitsOf(chart, Only(OptimizeStrategy.Audio));

        Assert.AreEqual(0, units.Single(u => u.Sources[0].StoredName == "guitar.ogg").KnownSavingBytes);
        Assert.AreEqual("audio/opus", units.Single(u => u.Sources[0].StoredName == "guitar.ogg").CodecKey);
        Assert.IsTrue(units.Single(u => u.Sources[0].StoredName == "rhythm.ogg").NeedsMeasuring);
        Assert.AreEqual("audio/vorbis", units.Single(u => u.Sources[0].StoredName == "rhythm.ogg").CodecKey);
    }
}
