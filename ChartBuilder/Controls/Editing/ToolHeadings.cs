using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The toolbar buttons' names with the keys that pick them (<see cref="EditorKeys.ToolKeys"/>), for their tips'
    /// headings, and the keys alone for their accelerators. Built from the one key table, so a tip never shows a key the
    /// editor doesn't take. Read from the window's XAML with <c>x:Static</c>.
    /// </summary>
    internal static class ToolHeadings
    {
        public static string Select { get; } = HeadingOf(EditorTool.Select, Strings.ToolSelect);
        public static string Place { get; } = HeadingOf(EditorTool.Place, Strings.ToolPlace);
        public static string Erase { get; } = HeadingOf(EditorTool.Erase, Strings.ToolErase);
        public static string Phrase { get; } = HeadingOf(EditorTool.Phrase, Strings.ToolPhrase);
        public static string Tempo { get; } = HeadingOf(EditorTool.Tempo, Strings.ToolTempo);
        public static string Sync { get; } = HeadingOf(EditorTool.Sync, Strings.ToolSync);

        public static string SelectKey { get; } = EditorKeys.ShortcutOf(EditorTool.Select)!;
        public static string PlaceKey { get; } = EditorKeys.ShortcutOf(EditorTool.Place)!;
        public static string EraseKey { get; } = EditorKeys.ShortcutOf(EditorTool.Erase)!;
        public static string PhraseKey { get; } = EditorKeys.ShortcutOf(EditorTool.Phrase)!;
        public static string TempoKey { get; } = EditorKeys.ShortcutOf(EditorTool.Tempo)!;
        public static string SyncKey { get; } = EditorKeys.ShortcutOf(EditorTool.Sync)!;

        /// <summary>The heading of <paramref name="tool"/>'s tip: its <paramref name="name"/> and the key that picks it.</summary>
        internal static string HeadingOf(EditorTool tool, string name) =>
            string.Format(Strings.ToolKeyed, name, EditorKeys.ShortcutOf(tool));
    }
}
