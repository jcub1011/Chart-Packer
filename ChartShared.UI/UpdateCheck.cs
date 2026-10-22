using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartShared.Core;
using ChartShared.Core.Settings;
using ChartShared.Core.Updating;
using ChartShared.UI.Resources;
using Serilog;

namespace ChartShared.UI
{
    /// <summary>
    /// The update check both apps run: one check at a time per process, its result cached in
    /// <c>update-settings.json</c> (<see cref="UserConfigPaths.UpdateSettingsFile"/>) so every version chip, in either
    /// app, shows it. A check runs when it's due under the chosen frequency, and again whenever a preference changes.
    /// </summary>
    public static class UpdateCheck
    {
        // Guards against several chips each starting a check at once: a non-forced check within this
        // of the last attempt reuses the cached result instead of hitting GitHub again.
        private static readonly TimeSpan AttemptDebounce = TimeSpan.FromMinutes(5);

        private static readonly HttpClient s_http = new();
        private static readonly SemaphoreSlim s_gate = new(1, 1);

        // Only touched on the UI thread.
        private static CancellationTokenSource? s_check;

        // Only touched while holding s_gate.
        private static DateTimeOffset s_lastAttemptUtc = DateTimeOffset.MinValue;

        /// <summary>Raised on the UI thread after each check ends, however it ended. Read <see cref="Available"/> for the result.</summary>
        public static event Action? Checked;

        /// <summary>
        /// Starts a check, unless one is running. With <paramref name="force"/>, cancels any running check and checks
        /// now, whether or not one is due: after a preference changes.
        /// </summary>
        public static void Start(bool force)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => Start(force));
                return;
            }

            if (s_check is not null)
            {
                if (!force)
                    return;
                s_check.Cancel();
            }

            var check = s_check = new CancellationTokenSource();
            _ = RunAsync(force, check);
        }

        /// <summary>
        /// The newer release to offer, from the cached result: null when checking is off, nothing newer was found, or
        /// what was found is a pre-release while pre-releases don't count.
        /// </summary>
        public static (string? Tag, bool Prerelease) Available()
        {
            var settings = new UpdateSettingsFile(UserConfigPaths.UpdateSettingsFile).Read();
            if (!settings.CheckForUpdates || !IsNewerThanCurrent(settings.CachedTag))
                return (null, false);
            if (settings.CachedPrerelease && !settings.IncludePrereleases)
                return (null, false);
            return (settings.CachedTag, settings.CachedPrerelease);
        }

        /// <summary>The Settings rows for the update check: on or off, whether pre-releases count, and how often. Both apps list them.</summary>
        public static SettingDefinition[] CreateSettings(SettingCategory category) =>
        [
            new ToggleSetting
            {
                Id = category.Id + ".updateCheck",
                Category = category,
                Label = SharedStrings.UpdateCheckLabel,
                Description = SharedStrings.UpdateCheckDescription,
                Keywords = SettingKeywords.Split(SharedStrings.UpdateCheckKeywords),
                DefaultValue = true,
                Get = () => SettingsFile().Read().CheckForUpdates,
                Set = check =>
                {
                    SettingsFile().SetCheckForUpdates(check);
                    Start(force: true);
                },
            },
            new ToggleSetting
            {
                Id = category.Id + ".updatePrereleases",
                Category = category,
                Label = SharedStrings.UpdateIncludePrereleasesLabel,
                Description = SharedStrings.UpdateIncludePrereleasesDescription,
                Keywords = SettingKeywords.Split(SharedStrings.UpdateIncludePrereleasesKeywords),
                RequiresId = category.Id + ".updateCheck",
                DefaultValue = false,
                Get = () => SettingsFile().Read().IncludePrereleases,
                Set = include =>
                {
                    SettingsFile().SetIncludePrereleases(include);
                    Start(force: true);
                },
            },
            new ChoiceSetting<UpdateCheckFrequency>
            {
                Id = category.Id + ".updateFrequency",
                Category = category,
                Label = SharedStrings.UpdateFrequencyLabel,
                Description = SharedStrings.UpdateFrequencyDescription,
                Keywords = SettingKeywords.Split(SharedStrings.UpdateFrequencyKeywords),
                RequiresId = category.Id + ".updateCheck",
                DefaultValue = UpdateCheckFrequency.Daily,
                Choices =
                [
                    new SettingChoice<UpdateCheckFrequency>(UpdateCheckFrequency.Hourly, SharedStrings.UpdateFrequencyHourly),
                    new SettingChoice<UpdateCheckFrequency>(UpdateCheckFrequency.Daily, SharedStrings.UpdateFrequencyDaily),
                    new SettingChoice<UpdateCheckFrequency>(UpdateCheckFrequency.OnStartup, SharedStrings.UpdateFrequencyOnStartup),
                ],
                Get = () => SettingsFile().Read().CheckFrequency,
                Set = frequency =>
                {
                    SettingsFile().SetCheckFrequency(frequency);
                    Start(force: true);
                },
            },
        ];

        private static UpdateSettingsFile SettingsFile() => new(UserConfigPaths.UpdateSettingsFile);

        private static async Task RunAsync(bool force, CancellationTokenSource check)
        {
            var token = check.Token;
            try
            {
                // A cancelled check still running holds the gate until it notices; wait for it rather than overlap.
                await s_gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await CheckAsync(force, token).ConfigureAwait(false);
                }
                finally
                {
                    s_gate.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Superseded by a newer check.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The update check failed");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (s_check == check)
                        s_check = null;
                    check.Dispose();
                    Checked?.Invoke();
                });
            }
        }

        private static async Task CheckAsync(bool force, CancellationToken token)
        {
            var file = SettingsFile();
            var settings = file.Read();
            if (!settings.CheckForUpdates)
                return;

            // A result found under the other pre-release setting, by this app or the other one, is out of date.
            bool stale = settings.CachedIncludePrereleases != settings.IncludePrereleases;
            if (!force && !stale
                && (DateTimeOffset.UtcNow - s_lastAttemptUtc < AttemptDebounce || !IsDue(settings.LastCheckedUtc, settings.CheckFrequency)))
                return;
            s_lastAttemptUtc = DateTimeOffset.UtcNow;

            var result = await GitHubUpdateChecker.CheckForUpdateAsync(
                s_http, AppVersion.Semantic, settings.IncludePrereleases, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // A failed check keeps the last result and its time, so the next due check tries again.
            if (!result.Succeeded)
                return;
            if (!file.SetCached(DateTimeOffset.UtcNow, result.Release?.Tag, result.Release?.Prerelease ?? false, settings.IncludePrereleases))
                Log.Warning("Couldn't cache the update check in {Path}", UserConfigPaths.UpdateSettingsFile);
        }

        private static bool IsNewerThanCurrent(string? tag)
        {
            if (SemanticVersion.TryParse(tag) is not { } latest)
                return false;
            if (SemanticVersion.TryParse(AppVersion.Semantic) is not { } current)
                return false;
            return latest.CompareTo(current) > 0;
        }

        /// <summary>Whether <paramref name="checkedUtc"/> is old enough to check again under <paramref name="frequency"/>.</summary>
        private static bool IsDue(DateTimeOffset? checkedUtc, UpdateCheckFrequency frequency) =>
            frequency switch
            {
                UpdateCheckFrequency.OnStartup => true,
                UpdateCheckFrequency.Hourly => checkedUtc is null || DateTimeOffset.UtcNow - checkedUtc >= TimeSpan.FromHours(1),
                _ => checkedUtc is null || DateTimeOffset.UtcNow - checkedUtc >= TimeSpan.FromHours(24),
            };
    }
}
