using ChartPacker.Core.Metadata;
using ChartPacker.Resources;

namespace ChartPacker.UI.ViewModels
{
    /// <summary>What a metadata key or value typed into the metadata editor must be for <c>song.ini</c> to hold it.</summary>
    public static class MetadataEditRules
    {
        /// <summary>The key of the song's length, which can be worked out from its files.</summary>
        public const string SongLengthKey = "song_length";

        /// <summary>Why <paramref name="value"/> can't be stored, or <see langword="null"/> when it can.</summary>
        public static string? ValidateValue(string value) =>
            value.Length == 0 ? SharedStrings.ValueEmpty
            : MetadataRules.ValueHasForbiddenCharacter(value) ? SharedStrings.ValueForbidden
            : null;

        /// <summary>Why <paramref name="key"/> can't be stored, or <see langword="null"/> when it can.</summary>
        public static string? ValidateKey(string key) =>
            key.Length == 0 ? SharedStrings.KeyEmpty
            : MetadataRules.KeyHasForbiddenCharacter(key) ? SharedStrings.KeyForbidden
            : null;
    }
}
