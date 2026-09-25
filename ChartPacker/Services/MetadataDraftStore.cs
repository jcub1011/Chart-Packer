using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Services
{
    /// <summary>
    /// Unsaved metadata changes for every song, keyed by the song's full path. They live only as
    /// long as the app is open, so switching selection keeps them and closing loses them.
    /// </summary>
    public sealed class MetadataDraftStore
    {
        private readonly Dictionary<string, MetadataDraft> _drafts = new(StringComparer.Ordinal);

        /// <summary>Raised with a song's path after its draft may have changed.</summary>
        public event Action<string>? DraftsChanged;

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

        /// <summary>Tells listeners that the draft for <paramref name="path"/> was edited.</summary>
        public void NotifyChanged(string path) => DraftsChanged?.Invoke(path);

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
