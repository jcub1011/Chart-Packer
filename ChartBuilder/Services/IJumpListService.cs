using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using ChartBuilder.Resources;
using ChartShared.Core.Launching;
using Serilog;

namespace ChartBuilder.Services
{
    /// <summary>The recent songs in the taskbar button's right-click menu, where the platform has one.</summary>
    public interface IJumpListService
    {
        /// <summary>Lists the recent songs again, as saved. Never throws, and doesn't wait: the shell does the work.</summary>
        void Refresh();
    }

    /// <summary>For platforms without a Jump List.</summary>
    public sealed class NullJumpListService : IJumpListService
    {
        public void Refresh()
        {
        }
    }

    /// <summary>The Windows taskbar's Jump List, which lists the recent songs under "Recent".</summary>
    /// <param name="settings">Where the recent songs are kept.</param>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsJumpListService(BuilderSettingsService settings) : IJumpListService
    {
        // What a Refresh saw, for the worker to publish.
        private sealed record Snapshot(List<RecentSong> Songs, string Category, string Exe);

        // The shell's list is replaced whole, so one worker publishes at a time, the newest snapshot last: a Refresh that
        // comes while it works replaces the one waiting, and the worker ends once nothing's waiting.
        private readonly Lock _gate = new();
        private Snapshot? _pending;
        private bool _running;

        private static ILogger Logger => Log.ForContext<WindowsJumpListService>();

        public void Refresh()
        {
            if (Environment.ProcessPath is not { } exe)
                return;

            // Copied here, on the thread that changes it; the shell is slow, and the songs' folders may be on a network.
            var snapshot = new Snapshot(settings.Settings.RecentSongs.ToList(), Strings.JumpListRecentCategory, exe);
            lock (_gate)
            {
                _pending = snapshot;
                if (_running)
                    return;

                _running = true;
            }

            var thread = new Thread(Work) { IsBackground = true, Name = "Jump List" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Work()
        {
            while (true)
            {
                Snapshot snapshot;
                lock (_gate)
                {
                    if (_pending is not { } next)
                    {
                        _running = false;
                        return;
                    }

                    snapshot = next;
                    _pending = null;
                }

                Publish(snapshot);
            }
        }

        private static void Publish(Snapshot snapshot)
        {
            try
            {
                WindowsJumpList.Publish(
                    snapshot.Category,
                    snapshot.Exe,
                    removed => RecentJumpList.Build(snapshot.Songs, path => Directory.Exists(path) || File.Exists(path), removed));
            }
            catch (Exception ex)
            {
                // The list is a convenience: the editor carries on without it.
                Logger.Warning(ex, "The taskbar's Jump List couldn't be updated");
            }
        }
    }
}
