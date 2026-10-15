using System.Collections.Generic;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.UI.ViewModels
{
    /// <summary>
    /// What <see cref="MetadataEditorViewModel"/> shows and edits: the songs' metadata, and where an edit goes. Chart
    /// Packer's inspector puts edits into its songs' drafts; Chart Builder's into the song it has open.
    /// </summary>
    public interface IMetadataEditorHost
    {
        /// <summary>Whether the songs can be edited now.</summary>
        bool CanEdit { get; }

        /// <summary>Whether several songs are shown, merged: a failure then says how many of them have it.</summary>
        bool IsBulk { get; }

        /// <summary>The songs shown: each one's metadata as stored, with what it'll be once its pending changes are saved.</summary>
        IReadOnlyList<MetadataItem> Items { get; }

        /// <summary>The parts the one song's chart has, by part id; null when that isn't known, as for several songs.</summary>
        IReadOnlyDictionary<string, ChartPart>? ChartParts { get; }

        /// <summary>Why there's no metadata to show, such as a folder without a song.ini; null for none.</summary>
        string? LoadNote { get; }

        /// <summary>Works out <c>song_length</c> from the songs' files; null where that isn't offered.</summary>
        IRelayCommand? AutoSetSongLengthCommand { get; }

        /// <summary>Whether <paramref name="key"/> can be changed, beside <see cref="CanEdit"/>: not when something else decides it.</summary>
        bool CanEditKey(string key);

        /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> in every song shown.</summary>
        void SetValue(string key, string value);

        /// <summary>Removes <paramref name="key"/> from every song shown.</summary>
        void RemoveKey(string key);

        /// <summary>Takes back the pending change to <paramref name="key"/> in every song shown.</summary>
        void RevertKey(string key);

        /// <summary>Unsets the difficulty <paramref name="key"/>: <c>-1</c> where one is stored, and no change elsewhere.</summary>
        void UnsetDifficulty(string key);

        /// <summary>Applies every quick fix for <paramref name="key"/>, each song's worked out from its own value.</summary>
        void ApplyQuickFixes(string key);
    }
}
