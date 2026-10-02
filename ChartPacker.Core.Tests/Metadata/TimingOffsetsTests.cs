using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Tests.Metadata;

[TestClass]
public sealed class TimingOffsetsTests
{
    [TestMethod]
    public void AudioLaterLowersTheDelayAndReadsBackAsTheOffset()
    {
        KeyValuePair<string, string>[] original = [P("name", "Song"), P("delay", "100")];
        var draft = new MetadataDraft();

        TimingOffsets.Set(draft, TimingOffsets.AudioKey, original, TimeSpan.Zero, 50);

        var drafted = draft.ApplyTo(original);
        Assert.AreEqual("50", drafted.Single(p => p.Key == "delay").Value);
        Assert.AreEqual(50, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, original, drafted, TimeSpan.Zero));
    }

    [TestMethod]
    public void VideoLaterLowersTheStartAndReadsBackAsTheOffset()
    {
        KeyValuePair<string, string>[] original = [P("name", "Song")];
        var draft = new MetadataDraft();

        TimingOffsets.Set(draft, TimingOffsets.VideoKey, original, TimeSpan.Zero, 50);

        var drafted = draft.ApplyTo(original);
        Assert.AreEqual("-50", drafted.Single(p => p.Key == "video_start_time").Value);
        Assert.AreEqual(50, TimingOffsets.OffsetOf(TimingOffsets.VideoKey, original, drafted, TimeSpan.Zero));
        Assert.AreEqual(TimeSpan.FromMilliseconds(-50), VideoTimeline.From(drafted).Start);
    }

    [TestMethod]
    public void ZeroForgetsTheChangeLeavingTheKeyAsStored()
    {
        KeyValuePair<string, string>[] stored = [P("delay", "+0100")];
        var draft = new MetadataDraft();
        TimingOffsets.Set(draft, TimingOffsets.AudioKey, stored, TimeSpan.Zero, 30);

        TimingOffsets.Set(draft, TimingOffsets.AudioKey, stored, TimeSpan.Zero, 0);

        Assert.IsTrue(draft.IsEmpty);
        Assert.AreSequenceEqual(stored, [.. draft.ApplyTo(stored)]);
    }

    [TestMethod]
    public void ZeroLeavesAnUnsetKeyUnset()
    {
        KeyValuePair<string, string>[] original = [P("name", "Song")];
        var draft = new MetadataDraft();
        TimingOffsets.Set(draft, TimingOffsets.VideoKey, original, TimeSpan.Zero, -20);

        TimingOffsets.Set(draft, TimingOffsets.VideoKey, original, TimeSpan.Zero, 0);

        Assert.IsTrue(draft.IsEmpty);
    }

    [TestMethod]
    public void AnUnsetDelayStartsFromTheChartsOwnOffset()
    {
        KeyValuePair<string, string>[] original = [P("name", "Song")];
        var chartOffset = TimeSpan.FromMilliseconds(250);
        var draft = new MetadataDraft();

        Assert.AreEqual(0, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, original, original, chartOffset));

        TimingOffsets.Set(draft, TimingOffsets.AudioKey, original, chartOffset, 50);

        var drafted = draft.ApplyTo(original);
        Assert.AreEqual("200", drafted.Single(p => p.Key == "delay").Value);
        Assert.AreEqual(50, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, original, drafted, chartOffset));
    }

    [TestMethod]
    public void TheKeyEditedDirectlyShowsAsAnOffset()
    {
        KeyValuePair<string, string>[] original = [P("Video_Start_Time", "1000")];
        var draft = new MetadataDraft();
        draft.Set("video_start_time", "1200");

        Assert.AreEqual(-200, TimingOffsets.OffsetOf(TimingOffsets.VideoKey, original, draft.ApplyTo(original), TimeSpan.Zero));
    }

    [TestMethod]
    public void AValueThatIsntANumberCountsAsUnset()
    {
        KeyValuePair<string, string>[] original = [P("delay", "soon")];
        KeyValuePair<string, string>[] drafted = [P("delay", "-40")];
        var chartOffset = TimeSpan.FromMilliseconds(60);

        Assert.AreEqual(100, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, original, drafted, chartOffset));
        Assert.AreEqual(-100, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, drafted, original, chartOffset));
    }

    [TestMethod]
    public void AnAudioOffsetPastTheLimitSetsTheFurthestDelayThatsStillRead()
    {
        KeyValuePair<string, string>[] original = [P("delay", "0")];
        var draft = new MetadataDraft();

        TimingOffsets.Set(draft, TimingOffsets.AudioKey, original, TimeSpan.Zero, 100_000_000);

        var drafted = draft.ApplyTo(original);
        Assert.AreEqual("-86399999", drafted.Single(p => p.Key == "delay").Value);
        Assert.AreEqual(TimeSpan.FromMilliseconds(-86_399_999), SongChartSettings.DelayOf(drafted, TimeSpan.FromMilliseconds(500)));
        Assert.AreEqual(86_399_999, TimingOffsets.OffsetOf(TimingOffsets.AudioKey, original, drafted, TimeSpan.Zero));
    }

    [TestMethod]
    public void AVideoOffsetPastTheLimitSetsTheFurthestStartThatsStillRead()
    {
        long max = (long)TimeSpan.FromDays(365).TotalMilliseconds;
        KeyValuePair<string, string>[] original = [P("video_start_time", (-max + 10).ToString(CultureInfo.InvariantCulture))];
        var draft = new MetadataDraft();

        TimingOffsets.Set(draft, TimingOffsets.VideoKey, original, TimeSpan.Zero, 50);

        var drafted = draft.ApplyTo(original);
        Assert.AreEqual((-max).ToString(CultureInfo.InvariantCulture), drafted.Single(p => p.Key == "video_start_time").Value);
        Assert.AreEqual(TimeSpan.FromMilliseconds(-max), VideoTimeline.From(drafted).Start);
        Assert.AreEqual(10, TimingOffsets.OffsetOf(TimingOffsets.VideoKey, original, drafted, TimeSpan.Zero));
    }

    [TestMethod]
    public void TheRangeIsTheOffsetsThatLeaveTheKeyReadable()
    {
        KeyValuePair<string, string>[] original = [P("delay", "100")];

        Assert.AreEqual((100 - 86_399_999, 100 + 86_399_999), TimingOffsets.RangeOf(TimingOffsets.AudioKey, original, TimeSpan.Zero));
        Assert.AreEqual((int.MinValue, int.MaxValue), TimingOffsets.RangeOf(TimingOffsets.VideoKey, original, TimeSpan.Zero));
    }

    [TestMethod]
    public void TheRangeAlwaysHoldsZero()
    {
        // An unset delay counts as the chart's own offset, which needn't be one a delay can be.
        var range = TimingOffsets.RangeOf(TimingOffsets.AudioKey, [], TimeSpan.FromDays(2));

        Assert.IsTrue(range.Min <= 0 && range.Max >= 0, $"{range} doesn't hold zero.");
    }

    [TestMethod]
    public void OtherKeysArentTimingKeys() =>
        Assert.ThrowsExactly<ArgumentException>(() => TimingOffsets.OffsetOf("song_length", [], [], TimeSpan.Zero));

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);
}
