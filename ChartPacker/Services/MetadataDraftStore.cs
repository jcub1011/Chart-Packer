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

        /// <summary>The draft was written to disk and cleared.</summary>
        Saved,
    }

    /// <summary>
    /// Unsaved metadata changes for every song, keyed by the song's full path. They live only as
    /// long as the app is open, so switching selection keeps them and closing loses them.
    /// </summary>
    public sealed class MetadataDraftStore
    {
        private readonly Dictionary<string, MetadataDraft> _drafts = new(StringComparer.Ordinal);

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

        /// <summary>Tells listeners that the draft for <paramref name="path"/> changed, and how.</summary>
        public void NotifyChanged(string path, DraftChange change = DraftChange.Edited) => DraftsChanged?.Invoke(path, change);

        /// <summary>Drops the drafts of songs that are no longer on disk.</summary>
        public void DiscardMissing()
        {
            foreach (string path in _drafts.Keys.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList())
            {
                bool hadChanges = !_drafts[path].IsEmpty;
                _drafts.Remove(path);
                if (hadChanges)
                    NotifyChanged(path);
            }
        }
    }
}
