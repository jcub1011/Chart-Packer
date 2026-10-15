using ChartShared.Core.Metadata;

namespace ChartShared.Core.Tests.Metadata;

[TestClass]
public sealed class VideoTimelineTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void FromReadsTheFirstOfEachKeyIgnoringCase()
    {
        var timeline = VideoTimeline.From([P("Video_Start_Time", "1500"), P("video_end_time", "8000"), P("VIDEO_LOOP", "True"), P("video_start_time", "99")]);

        Assert.AreEqual(new VideoTimeline(TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(8), true), timeline);
    }

    [TestMethod]
    public void FromIgnoresMissingAndInvalidValues()
    {
        Assert.AreEqual(VideoTimeline.Default, VideoTimeline.From([]));
        Assert.AreEqual(VideoTimeline.Default, VideoTimeline.From([P("video_start_time", "soon"), P("video_end_time", "-1"), P("video_loop", "no")]));
        Assert.AreEqual(VideoTimeline.Default, VideoTimeline.From([P("video_start_time", long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
        Assert.IsTrue(VideoTimeline.From([P("video_loop", "1")]).Loop);
    }

    [TestMethod]
    public void NoOffsetFollowsTheSong() =>
        Assert.AreEqual(TimeSpan.FromSeconds(3), VideoTimeline.Default.Map(TimeSpan.FromSeconds(3), Ten));

    [TestMethod]
    public void PositiveStartSkipsIntoTheVideo()
    {
        var timeline = new VideoTimeline(TimeSpan.FromSeconds(2), null, false);

        Assert.AreEqual(TimeSpan.FromSeconds(2), timeline.Map(TimeSpan.Zero, Ten));
        Assert.AreEqual(TimeSpan.FromSeconds(5), timeline.Map(TimeSpan.FromSeconds(3), Ten));
    }

    [TestMethod]
    public void NegativeStartHoldsTheFirstFrame()
    {
        var timeline = new VideoTimeline(TimeSpan.FromSeconds(-2), null, false);

        Assert.AreEqual(TimeSpan.Zero, timeline.Map(TimeSpan.FromSeconds(1), Ten));
        Assert.AreEqual(TimeSpan.FromSeconds(1), timeline.Map(TimeSpan.FromSeconds(3), Ten));
    }

    [TestMethod]
    public void PastTheEndHoldsTheLastFrame()
    {
        Assert.AreEqual(Ten, VideoTimeline.Default.Map(TimeSpan.FromSeconds(30), Ten));
        Assert.AreEqual(TimeSpan.FromSeconds(4), new VideoTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(4), false).Map(TimeSpan.FromSeconds(6), Ten));
    }

    [TestMethod]
    public void AnEndPastTheVideoIsTheVideosEnd() =>
        Assert.AreEqual(Ten, new VideoTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(60), false).Map(TimeSpan.FromSeconds(30), Ten));

    [TestMethod]
    public void LoopingWrapsBetweenTheStartAndTheEnd()
    {
        var timeline = new VideoTimeline(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), true);

        // Song 0 → video 2, song 4 → video 6 (the end) → wraps to 2; song 5 → 3.
        Assert.AreEqual(TimeSpan.FromSeconds(2), timeline.Map(TimeSpan.FromSeconds(4), Ten));
        Assert.AreEqual(TimeSpan.FromSeconds(3), timeline.Map(TimeSpan.FromSeconds(5), Ten));
    }

    [TestMethod]
    public void LoopingTheWholeVideoWrapsToTheStart() =>
        Assert.AreEqual(TimeSpan.FromSeconds(5), new VideoTimeline(TimeSpan.Zero, null, true).Map(TimeSpan.FromSeconds(25), Ten));

    [TestMethod]
    public void AnUnknownLengthIsNotClamped() =>
        Assert.AreEqual(TimeSpan.FromSeconds(30), VideoTimeline.Default.Map(TimeSpan.FromSeconds(30), TimeSpan.Zero));

    private static KeyValuePair<string, string> P(string key, string value) => KeyValuePair.Create(key, value);
}
