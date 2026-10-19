using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartPacker.Resources;
using ChartShared.Core.Scanning;

namespace ChartPacker.Services
{
    /// <summary>
    /// Chart Packer's song locks (charter-spec §4.8). A song is held while it has unsaved changes and while it's written
    /// to, so no Chart Builder or other Chart Packer edits it meanwhile; and the songs shown are watched, so their files
    /// can be let go of, and editing them stopped, while another holds one.
    /// </summary>
    /// <remarks>Used on the UI thread, but for <see cref="Set"/>, which a job uses from its own.</remarks>
    public sealed class SongLocks
    {
        // How often the songs watched are checked.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        private readonly MetadataDraftStore _drafts;

        // The hold each song with unsaved changes has, by its draft's path.
        private readonly Dictionary<string, IDisposable> _draftHolds = new(StringComparer.Ordinal);

        // The songs watched, by SongLock.KeyOf.
        private readonly Dictionary<string, Watched> _watched = new(StringComparer.Ordinal);

        private readonly DispatcherTimer _timer;
        private bool _polling;
        private bool _pollQueued;

        // Completed by the poll that leaves every song watched checked, for CheckedAsync.
        private TaskCompletionSource _checked = NewChecked();

        public SongLocks(MetadataDraftStore drafts)
        {
            _drafts = drafts;
            _drafts.DraftsChanged += OnDraftsChanged;
            _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => _ = PollAsync());
            _timer.Stop();
        }

        /// <summary>The locks this process holds, for a job to hold what it writes.</summary>
        public SongLockSet Set { get; } = new(SongLockHolder.ChartPacker);

        /// <summary>Raised, on the UI thread, with the path it was watched by, when who else holds a watched song changes.</summary>
        public event Action<string>? HolderChanged;

        /// <summary>
        /// Raised when a song's unsaved changes were dropped because they couldn't hold it, with its path and why, as a
        /// sentence: another process took it before the change could hold it, or its lock couldn't be taken.
        /// </summary>
        public event Action<string, string>? ChangeRefused;

        /// <summary>
        /// Who else holds the song at <paramref name="path"/>: as last checked when it's watched (none until it's first
        /// checked; see <see cref="CheckedAsync"/>), otherwise checked now. <see langword="null"/> when no one does, or
        /// this process does.
        /// </summary>
        public SongLockHolder? HolderElsewhere(string path)
        {
            try
            {
                if (_watched.TryGetValue(SongLock.KeyOf(path), out Watched? watched))
                    return watched.Holder;
            }
            catch (ArgumentException)
            {
                return null;
            }

            return Set.HolderElsewhere(path);
        }

        /// <summary>
        /// Watches the song at <paramref name="path"/>, checked off the UI thread soon after, and then about once a
        /// second, until the registration is disposed.
        /// </summary>
        public IDisposable Watch(string path)
        {
            string key = SongLock.KeyOf(path);
            if (!_watched.TryGetValue(key, out Watched? watched))
            {
                watched = new Watched(path);
                _watched.Add(key, watched);
                _timer.Start();
                if (_checked.Task.IsCompleted)
                    _checked = NewChecked();

                // Once every song watched just now is, so they're checked together.
                if (!_pollQueued)
                {
                    _pollQueued = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _pollQueued = false;
                        _ = PollAsync();
                    }, DispatcherPriority.Background);
                }
            }

            watched.Count++;
            return new Registration(this, key);
        }

        /// <summary>Completes once every song watched has been checked at least once.</summary>
        public Task CheckedAsync() => _checked.Task;

        /// <summary>Holds every one of <paramref name="paths"/> for a write, or none of them.</summary>
        /// <param name="refused">The songs another process holds, and who holds each, when they can't all be held.</param>
        /// <returns>The holds, until disposed; <see langword="null"/> when another process holds any of the songs.</returns>
        /// <exception cref="System.IO.IOException">A song's lock can't be taken, though it can be written to.</exception>
        public IDisposable? TryHold(IEnumerable<string> paths, out IReadOnlyList<(string Path, SongLockHolder Holder)> refused)
        {
            var holds = new List<IDisposable>();
            var notHeld = new List<(string, SongLockHolder)>();
            try
            {
                foreach (string path in paths)
                {
                    if (Set.TryHold(path) is { } hold)
                        holds.Add(hold);
                    else
                        notHeld.Add((path, Set.HolderElsewhere(path) ?? SongLockHolder.Unknown));
                }
            }
            catch
            {
                foreach (var hold in holds)
                    hold.Dispose();
                throw;
            }

            refused = notHeld;
            if (notHeld.Count == 0)
                return new Holds(holds);

            foreach (var hold in holds)
                hold.Dispose();
            return null;
        }

        /// <summary>Says who holds <paramref name="name"/>, as a sentence.</summary>
        public static string Describe(string name, SongLockHolder holder)
        {
            string format = holder switch
            {
                SongLockHolder.ChartBuilder => Strings.SongInUseByBuilderFormat,
                SongLockHolder.ChartPacker => Strings.SongInUseByPackerFormat,
                _ => Strings.SongInUseElsewhereFormat,
            };
            return string.Format(CultureInfo.CurrentCulture, format, name);
        }

        /// <summary>Says which of the songs written are held elsewhere, as a sentence: the song, or how many.</summary>
        /// <param name="nameOf">The name to give the song at a path.</param>
        public static string Describe(IReadOnlyList<(string Path, SongLockHolder Holder)> refused, Func<string, string> nameOf) =>
            refused is [var one]
                ? Describe(nameOf(one.Path), one.Holder)
                : string.Format(CultureInfo.CurrentCulture, Strings.SongsInUseFormat, refused.Count);

        // A draft that's gained changes holds its song, and one that's lost them lets go.
        private void OnDraftsChanged(string path, DraftChange change)
        {
            bool hasChanges = _drafts.HasDrafts(path);
            if (hasChanges == _draftHolds.ContainsKey(path))
                return;

            if (!hasChanges)
            {
                _draftHolds.Remove(path, out IDisposable? hold);
                hold?.Dispose();
                return;
            }

            string reason;
            try
            {
                if (Set.TryHold(path) is { } taken)
                {
                    _draftHolds.Add(path, taken);
                    return;
                }

                // Taken elsewhere since the song was last checked: the change can't be kept, or saving it would overwrite theirs.
                reason = Describe(System.IO.Path.GetFileName(path), Set.HolderElsewhere(path) ?? SongLockHolder.Unknown);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                // Nothing can keep another from writing it meanwhile, so the change can't be kept either.
                reason = string.Format(CultureInfo.CurrentCulture, Strings.SongLockFailedFormat, System.IO.Path.GetFileName(path), e.Message);
            }

            _drafts.Get(path).Clear();
            _drafts.NotifyChanged(path);
            ChangeRefused?.Invoke(path, reason);
        }

        // Checks every song watched off the UI thread, then says which changed.
        private async Task PollAsync()
        {
            if (_polling)
                return;

            _polling = true;
            bool polled = false;
            try
            {
                var watched = _watched.ToList();
                var paths = watched.Select(w => w.Value.Path).ToList();
                var holders = await Task.Run(() => Set.HoldersElsewhere(paths));

                var changed = new List<string>();
                for (int i = 0; i < watched.Count; i++)
                {
                    var (key, entry) = watched[i];
                    if (!ReferenceEquals(_watched.GetValueOrDefault(key), entry))
                        continue;
                    entry.Checked = true;
                    if (entry.Holder == holders[i])
                        continue;
                    entry.Holder = holders[i];
                    changed.Add(entry.Path);
                }

                polled = true;
                foreach (string path in changed)
                    HolderChanged?.Invoke(path);
            }
            finally
            {
                _polling = false;
                CompleteIfChecked();
            }

            // Songs watched while this checked are checked now, rather than at the next tick.
            if (polled && !_checked.Task.IsCompleted)
                _ = PollAsync();
        }

        // Lets CheckedAsync's callers go once no song watched is still to be checked; one watched while this checked is
        // checked by the next poll, queued as it was watched.
        private void CompleteIfChecked()
        {
            if (_watched.Values.All(w => w.Checked))
                _checked.TrySetResult();
        }

        private void Unwatch(string key)
        {
            if (!_watched.TryGetValue(key, out Watched? watched) || --watched.Count > 0)
                return;

            _watched.Remove(key);
            if (_watched.Count == 0)
                _timer.Stop();
            CompleteIfChecked();
        }

        private static TaskCompletionSource NewChecked()
        {
            var checkedSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            checkedSource.SetResult();
            return checkedSource;
        }

        // A song watched: by the path first given, who else held it when last checked, whether it has been, and how many watch it.
        private sealed class Watched(string path)
        {
            public string Path { get; } = path;

            public SongLockHolder? Holder { get; set; }

            public bool Checked { get; set; }

            public int Count { get; set; }
        }

        private sealed class Registration(SongLocks locks, string key) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                locks.Unwatch(key);
            }
        }

        private sealed class Holds(List<IDisposable> holds) : IDisposable
        {
            public void Dispose()
            {
                foreach (var hold in holds)
                    hold.Dispose();
                holds.Clear();
            }
        }
    }
}
