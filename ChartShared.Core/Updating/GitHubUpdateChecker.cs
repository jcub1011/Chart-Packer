using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChartShared.Core.Updating;

/// <summary>A GitHub release as the update check reads it: its tag and whether it's a pre-release or draft.</summary>
/// <param name="Tag">The release tag, e.g. <c>v1.2.0</c>.</param>
/// <param name="Prerelease">Whether GitHub marks it a pre-release.</param>
/// <param name="Draft">Whether GitHub marks it a draft.</param>
public sealed record GitHubRelease(string Tag, bool Prerelease, bool Draft);

/// <summary>How an update check went: whether GitHub answered, and if so the newest newer release it listed.</summary>
/// <param name="Succeeded">Whether the releases were fetched and read. False when offline, rate-limited and the like.</param>
/// <param name="Release">The newest release newer than the running version, or null when there's none or the check failed.</param>
public sealed record UpdateCheckResult(bool Succeeded, GitHubRelease? Release)
{
    /// <summary>A check that couldn't reach or read GitHub.</summary>
    public static UpdateCheckResult Failed { get; } = new(false, null);
}

/// <summary>Source-generated JSON for the GitHub releases response (Native AOT: no reflection).</summary>
[JsonSerializable(typeof(List<GitHubReleaseJson>))]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext
{
}

internal sealed class GitHubReleaseJson
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }
}

/// <summary>
/// Finds the newest GitHub release newer than the running version.
/// Drafts never count; pre-releases count only while <paramref name="includePrereleases"/>.
/// </summary>
public static class GitHubUpdateChecker
{
    /// <summary>The releases list for the repo the releases page links to.</summary>
    public const string ReleasesApiUrl = "https://api.github.com/repos/jcub1011/Chart-Packer/releases?per_page=100";

    /// <summary>
    /// The newest release in <paramref name="releases"/> newer than <paramref name="currentVersion"/>,
    /// or null when none is. <paramref name="currentVersion"/> like <c>1.2.0</c> or <c>1.2.0-beta.1</c>.
    /// </summary>
    public static GitHubRelease? NewestNewerThan(
        IEnumerable<GitHubRelease> releases, string currentVersion, bool includePrereleases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (SemanticVersion.TryParse(currentVersion) is not { } current)
            return null;

        GitHubRelease? best = null;
        SemanticVersion? bestVersion = null;
        foreach (var release in releases)
        {
            if (release.Draft)
                continue;
            if (release.Prerelease && !includePrereleases)
                continue;
            if (string.IsNullOrWhiteSpace(release.Tag))
                continue;
            if (SemanticVersion.TryParse(release.Tag) is not { } version)
                continue;
            if (version.CompareTo(current) <= 0)
                continue;
            if (bestVersion is null || version.CompareTo(bestVersion) > 0)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// Fetches the releases and gives the newest one newer than <paramref name="currentVersion"/>.
    /// Failures are traced and give <see cref="UpdateCheckResult.Failed"/>, never thrown; only
    /// <paramref name="cancellationToken"/> being cancelled throws.
    /// </summary>
    /// <param name="http">The client to fetch with; the caller owns it. Must not be null.</param>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        HttpClient http, string currentVersion, bool includePrereleases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ChartPacker", currentVersion));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"Update check failed: GitHub gave {(int)response.StatusCode} {response.ReasonPhrase}.");
                return UpdateCheckResult.Failed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var json = await JsonSerializer.DeserializeAsync(
                stream, GitHubReleaseJsonContext.Default.ListGitHubReleaseJson, cancellationToken).ConfigureAwait(false);
            if (json is null)
                return UpdateCheckResult.Failed;

            var releases = json
                .Where(r => !string.IsNullOrWhiteSpace(r.TagName))
                .Select(r => new GitHubRelease(r.TagName!, r.Prerelease, r.Draft));
            return new UpdateCheckResult(true, NewestNewerThan(releases, currentVersion, includePrereleases));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A TaskCanceledException without our token cancelled is the client timing out: a failure like any other.
            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return UpdateCheckResult.Failed;
        }
    }
}
