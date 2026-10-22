using ChartShared.Core.Updating;

namespace ChartShared.Core.Tests.Updating;

[TestClass]
public sealed class GitHubUpdateCheckerTests
{
    private static GitHubRelease Release(string tag, bool prerelease = false, bool draft = false) =>
        new(tag, prerelease, draft);

    [TestMethod]
    public void PicksTheNewestNewerStable()
    {
        var releases = new[]
        {
            Release("v1.2.0"),
            Release("v1.3.0"),
            Release("v1.1.0"),
        };
        var newest = GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: false);
        Assert.IsNotNull(newest);
        Assert.AreEqual("v1.3.0", newest.Tag);
    }

    [TestMethod]
    public void IgnoresCurrentAndOlder()
    {
        var releases = new[] { Release("v1.2.0"), Release("v1.1.0") };
        Assert.IsNull(GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: false));
    }

    [TestMethod]
    public void IgnoresDraftsAndUnparsableTags()
    {
        var releases = new[]
        {
            Release("v9.9.9", draft: true),
            Release("not-a-version"),
            Release("", prerelease: true),
        };
        Assert.IsNull(GitHubUpdateChecker.NewestNewerThan(releases, "1.0.0", includePrereleases: true));
    }

    [TestMethod]
    public void PrereleasesCountOnlyWhenIncluded()
    {
        var releases = new[] { Release("v1.3.0-beta.1", prerelease: true) };
        Assert.IsNull(GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: false));
        var newest = GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: true);
        Assert.IsNotNull(newest);
        Assert.IsTrue(newest.Prerelease);
    }

    [TestMethod]
    public void PrefersStableOverItsOwnPrerelease()
    {
        // Both newer than current; the stable 1.3.0 outranks a higher-numbered pre-release candidate
        // only by version order — here 1.4.0-beta is genuinely newer, so it wins when included.
        var releases = new[]
        {
            Release("v1.3.0"),
            Release("v1.4.0-beta.1", prerelease: true),
        };
        var stableOnly = GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: false);
        Assert.AreEqual("v1.3.0", stableOnly?.Tag);
        var withPre = GitHubUpdateChecker.NewestNewerThan(releases, "1.2.0", includePrereleases: true);
        Assert.AreEqual("v1.4.0-beta.1", withPre?.Tag);
    }

    [TestMethod]
    public void OrdersPrereleaseNumbersAsNumbers()
    {
        var releases = new[]
        {
            Release("v1.3.0-beta.10", prerelease: true),
            Release("v1.3.0-beta.9", prerelease: true),
        };
        var newest = GitHubUpdateChecker.NewestNewerThan(releases, "1.3.0-beta.2", includePrereleases: true);
        Assert.AreEqual("v1.3.0-beta.10", newest?.Tag);
    }

    [TestMethod]
    public async Task CheckFetchesReleasesAndPicksNewest()
    {
        string json = """
            [
              { "tag_name": "v0.5.0", "prerelease": false, "draft": false },
              { "tag_name": "v0.6.0", "prerelease": false, "draft": false },
              { "tag_name": "v0.7.0-beta.1", "prerelease": true, "draft": false }
            ]
            """;
        using var http = new HttpClient(new JsonHandler(json));
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(http, "0.5.0", includePrereleases: false);
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Release);
        Assert.AreEqual("v0.6.0", result.Release.Tag);
    }

    [TestMethod]
    public async Task CheckWithNothingNewerSucceedsEmpty()
    {
        using var http = new HttpClient(new JsonHandler("""[ { "tag_name": "v0.5.0", "prerelease": false, "draft": false } ]"""));
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(http, "0.5.0", includePrereleases: false);
        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task CheckFailureIsNotSuccess()
    {
        using var http = new HttpClient(new FailingHandler());
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(http, "0.5.0", includePrereleases: false);
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task RateLimitedCheckIsNotSuccess()
    {
        using var http = new HttpClient(new JsonHandler("""{ "message": "API rate limit exceeded" }""", System.Net.HttpStatusCode.Forbidden));
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(http, "0.5.0", includePrereleases: false);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task CancelledCheckThrows()
    {
        using var http = new HttpClient(new JsonHandler("[]"));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => GitHubUpdateChecker.CheckForUpdateAsync(http, "0.5.0", includePrereleases: false, cancel.Token));
    }

    private sealed class JsonHandler(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.IsTrue(request.Headers.UserAgent.Count > 0, "GitHub requires a User-Agent.");
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Offline.");
    }
}
