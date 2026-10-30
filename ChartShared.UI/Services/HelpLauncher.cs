using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ChartShared.Core.Help;
using Serilog;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Opens the project's documentation files in the browser, at this version's tag (see <see cref="HelpLinks"/>): the help
    /// pages (<see cref="HelpPages"/>) and the third-party notices. Failures are logged and reported as <c>false</c>; what to
    /// do about one is the caller's.
    /// </summary>
    public static class HelpLauncher
    {
        /// <summary>Opens the help page <paramref name="page"/>, one of <see cref="HelpPages"/>; the front page when null or blank. False when it couldn't be opened.</summary>
        public static Task<bool> OpenPageAsync(TopLevel? top, string? page)
        {
            string name = string.IsNullOrWhiteSpace(page) ? HelpPages.Home : page.Trim();
            return OpenAsync(top?.Launcher, () => HelpLinks.RefAsync(), gitRef => HelpPages.UrlFor(name, gitRef), name);
        }

        /// <summary>Opens the third-party notices file. False when it couldn't be opened.</summary>
        public static Task<bool> OpenNoticesAsync(TopLevel? top) =>
            OpenAsync(top?.Launcher, () => HelpLinks.RefAsync(), HelpPages.NoticesUrlFor, "the third-party notices");

        internal static async Task<bool> OpenAsync(ILauncher? launcher, Func<Task<string>> refAsync, Func<string, string> urlFor, string what)
        {
            string? url = null;
            try
            {
                url = urlFor(await refAsync());
                if (launcher is not null && await launcher.LaunchUriAsync(new Uri(url)))
                    return true;

                Log.Warning("Couldn't open {Page} ({Url})", what, url);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Couldn't open {Page} ({Url})", what, url);
            }

            return false;
        }
    }
}
