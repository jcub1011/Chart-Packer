using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChartShared.Core.Help;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Where this version's help pages are: its tag once the release has one, its release branch until then, and the main branch
    /// when it has neither.
    /// </summary>
    public static class HelpLinks
    {
        // How long an answer that couldn't be had (offline) is kept, so presses in a row don't each wait out the timeout.
        private static readonly TimeSpan Inconclusive = TimeSpan.FromSeconds(30);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

        private static readonly object Gate = new();

        // The one lookup there is: presses while it runs share it, and a definite answer is kept, as a tag doesn't come and go
        // while the app runs.
        private static Task<Found>? _lookup;

        private readonly record struct Found(string Ref, bool Definite, DateTime At);

        /// <summary>
        /// The tag of this version if it exists, where a release has its help as it shipped; otherwise its release branch, where the
        /// help is while the release is tested; otherwise the main branch. The tag when it can't be found out (offline), as a
        /// release has one.
        /// </summary>
        public static async Task<string> RefAsync(CancellationToken cancellationToken = default)
        {
            Task<Found> lookup;
            lock (Gate)
            {
                if (_lookup is null || IsStale(_lookup))
                    _lookup = LookUpAsync(AppVersion.Semantic);
                lookup = _lookup;
            }

            // The lookup isn't the caller's to cancel, as others may be waiting on it.
            return (await lookup.WaitAsync(cancellationToken).ConfigureAwait(false)).Ref;
        }

        // Dropped when it failed, or when it was inconclusive long enough ago to ask again.
        private static bool IsStale(Task<Found> lookup)
        {
            if (!lookup.IsCompleted)
                return false;
            if (!lookup.IsCompletedSuccessfully)
                return true;
            return !lookup.Result.Definite && DateTime.UtcNow - lookup.Result.At > Inconclusive;
        }

        private static async Task<Found> LookUpAsync(string version)
        {
            bool? tag = await ExistsAsync(HelpPages.TagFor(version)).ConfigureAwait(false);
            if (tag != false)
                return new Found(HelpPages.RefFor(version, tag), tag is not null, DateTime.UtcNow);

            bool? branch = await ExistsAsync(HelpPages.BranchFor(version)).ConfigureAwait(false);
            return new Found(HelpPages.RefFor(version, tag, branch), branch is not null, DateTime.UtcNow);
        }

        // A page of the ref is there when the ref is. Null when GitHub couldn't be asked, or answered with something else.
        private static async Task<bool?> ExistsAsync(string gitRef)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, HelpPages.UrlFor(HelpPages.Home, gitRef));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                return response.StatusCode switch
                {
                    HttpStatusCode.OK => true,
                    HttpStatusCode.NotFound => false,
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }
    }
}
