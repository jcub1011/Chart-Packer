using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChartShared.Core.Metadata;

namespace ChartPacker.Services
{
    /// <summary>What happened to a song's draft, so a panel showing the song knows how much to read again.</summary>
    public enum DraftChange
    {
        /// <summary>Anything may have changed, staged files included.</summary>
        Edited,

        /// <summary>Only metadata keys changed: what's shown can be rebuilt from the shared draft.</summary>
        Keys,

        /// <summary>The draft was written to disk, or cleared for good: there's nothing earlier to undo to.</summary>
        Saved,
    }

    /// <summary>
    /// Unsaved metadata changes for every song, keyed by the song's full path. They live only as
    /// long as the app is open, so switching selection keeps them and closing loses them. Each song's draft has an
    /// undo history of its own, which follows the changes announced by <see cref="NotifyChanged"/>.
    /// </summary>
    public sealed class MetadataDraftStore
    {
        private readonly Dictionary<string, MetadataDraft> _drafts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DraftHistory> _histories = new(StringComparer.Ordinal);

        /// <summary>Raised with a song's path, and what happened, after its draft may have changed.</summary>
        public event Action<string, DraftChange>? DraftsChanged;

        /// <summary>How many songs have unsaved changes.</summary>
        public int DirtyCount => _drafts.Values.Count(d => !d.IsEmpty);

        /// <summary>The draft for <paramref name="path"/>, created empty on first use.</summary>
        public MetadataDraft Get(string path)
        {
            if (!_drafts.TryGetValue(path, out var draft))
            {
                draft = new MetadataDraft();
                _drafts.Add(path, draft);
            }

            return draft;
        }

        /// <summary>Whether <paramref name="path"/> has unsaved changes.</summary>
        public bool HasDrafts(string path) => _drafts.TryGetValue(path, out var draft) && !draft.IsEmpty;

        /// <summary>
        /// Tells listeners that the draft for <paramref name="path"/> changed, and how. The change becomes a step to undo
        /// for that song alone, unless the draft is as it was, or the song was saved, which is the state undo can't go
        /// back past.
        /// </summary>
        /// <param name="mergeKey">
        /// Names a run of changes that make one step, as the many a drag makes; see <see cref="DraftHistory.Record"/>.
        /// </param>
        public void NotifyChanged(string path, DraftChange change = DraftChange.Edited, string? mergeKey = null)
        {
            if (change == DraftChange.Saved)
                ResetHistory(path);
            else if (_drafts.TryGetValue(path, out var draft))
            {
                // Made with the first change, as a draft starts empty and most are never changed.
                if (!_histories.TryGetValue(path, out var history))
                    _histories.Add(path, history = new DraftHistory(draft, baseline: new MetadataDraft()));

                history.Record(mergeKey);
            }

            DraftsChanged?.Invoke(path, change);
        }

        /// <summary>Whether <paramref name="path"/> has a change to undo.</summary>
        public bool CanUndo(string path) => _histories.TryGetValue(path, out var history) && history.CanUndo;

        /// <summary>Whether <paramref name="path"/> has a change undone to apply again.</summary>
        public bool CanRedo(string path) => _histories.TryGetValue(path, out var history) && history.CanRedo;

        /// <summary>Undoes the last change to the draft for <paramref name="path"/>, and no other song's.</summary>
        public void Undo(string path) => Move(path, undo: true);

        /// <summary>Applies again the last change undone to the draft for <paramref name="path"/>.</summary>
        public void Redo(string path) => Move(path, undo: false);

        private void Move(string path, bool undo)
        {
            if (!_histories.TryGetValue(path, out var history))
                return;

            // Not through NotifyChanged, which would take the draft's new state for a change to undo.
            bool onlyKeys = undo ? history.Undo() : history.Redo();
            DraftsChanged?.Invoke(path, onlyKeys ? DraftChange.Keys : DraftChange.Edited);
        }

        /// <summary>
        /// Forgets what can be undone for <paramref name="path"/>, making its draft as it is now the state to undo to:
        /// for when the song itself has changed, so earlier states of its draft no longer fit it.
        /// </summary>
        public void ResetHistory(string path)
        {
            if (_histories.TryGetValue(path, out var history))
                history.Reset();
        }

        /// <summary>Clears every song's unsaved changes, as when the user agrees to lose them.</summary>
        public void DiscardAll()
        {
            foreach (var (path, draft) in _drafts.Where(d => !d.Value.IsEmpty).ToList())
            {
                draft.Clear();
                NotifyChanged(path, DraftChange.Saved);
            }
        }

        /// <summary>
        /// Forgets what can be undone for every song with no unsaved changes: they aren't held, so they may have changed
        /// on disk since, as when the library is read again, and earlier states of their drafts may no longer fit them.
        /// </summary>
        public void ResetEmptyHistories()
        {
            foreach (var (path, history) in _histories)
            {
                if (_drafts[path].IsEmpty)
                    history.Reset();
            }
        }

        /// <summary>Drops the drafts of songs that are no longer on disk.</summary>
        public void DiscardMissing()
        {
            foreach (string path in _drafts.Keys.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList())
            {
                bool hadChanges = !_drafts[path].IsEmpty;
                _drafts.Remove(path);
                _histories.Remove(path);
                if (hadChanges)
                    NotifyChanged(path);
            }
        }
    }
}
