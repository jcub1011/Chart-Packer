using System.Collections.Generic;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Resources;

namespace ChartShared.UI.Keybinds
{
    /// <summary>
    /// The actions both apps have keys for, so a key rebound for one (Save, say) is rebound for the other. Each app registers
    /// the handlers of the ones its windows have.
    /// </summary>
    public static class SharedKeys
    {
        public const string Save = "file.save";
        public const string Settings = "app.settings";
        public const string Undo = "edit.undo";
        public const string Redo = "edit.redo";
        public const string PlayPause = "media.playPause";
        public const string NextChange = "reduction.nextChange";
        public const string PreviousChange = "reduction.previousChange";
        public const string FocusSearch = "settings.focusSearch";
        public const string Help = "help.open";

        /// <summary>A window showing a reduction's preview, Chart Packer's lower difficulties and Chart Builder's Reduce.</summary>
        public static readonly KeyScope ReductionPreview = new("shared.reductionPreview");

        /// <summary>The Settings window, both apps'.</summary>
        public static readonly KeyScope SettingsWindow = new("shared.settingsWindow");

        /// <summary>The shared actions, in the order they're listed in.</summary>
        public static IEnumerable<KeyAction> Actions()
        {
            string file = SharedStrings.KeyCategoryFile;
            string edit = SharedStrings.KeyCategoryEdit;
            string playback = SharedStrings.KeyCategoryPlayback;

            yield return new KeyAction
            {
                Id = Save, Name = SharedStrings.KeyActionSave, Category = file, Scope = KeyScope.Global,
                Defaults = KeyChord.List("Ctrl+S"),
            };
            yield return new KeyAction
            {
                Id = Settings, Name = SharedStrings.KeyActionSettings, Category = file, Scope = KeyScope.Global,
                Defaults = KeyChord.List("Ctrl+OemComma"),
            };

            // A text box handles Undo and Redo itself first, so these only run when focus isn't in one. Fixed, like the clipboard.
            yield return new KeyAction
            {
                Id = Undo, Name = SharedStrings.KeyActionUndo, Category = edit, Scope = KeyScope.Global,
                Defaults = KeyChord.List("Ctrl+Z"), Locked = true, YieldsToTextInput = true,
            };
            yield return new KeyAction
            {
                Id = Redo, Name = SharedStrings.KeyActionRedo, Category = edit, Scope = KeyScope.Global,
                Defaults = KeyChord.List("Ctrl+Y", "Ctrl+Shift+Z"), Locked = true, YieldsToTextInput = true,
            };

            yield return new KeyAction
            {
                Id = PlayPause, Name = SharedStrings.KeyActionPlayPause, Category = playback, Scope = KeyScope.Global,
                Defaults = KeyChord.List("Space"), AllowBare = true, ReservedDuringPlaytest = true,
            };
            yield return new KeyAction
            {
                Id = NextChange, Name = SharedStrings.KeyActionNextChange, Category = playback, Scope = ReductionPreview,
                Defaults = KeyChord.List("N"), AllowBare = true,
            };
            yield return new KeyAction
            {
                Id = PreviousChange, Name = SharedStrings.KeyActionPreviousChange, Category = playback, Scope = ReductionPreview,
                Defaults = KeyChord.List("Shift+N"),
            };

            yield return new KeyAction
            {
                Id = FocusSearch, Name = SharedStrings.KeyActionFocusSearch, Category = SharedStrings.KeyCategorySettings, Scope = SettingsWindow,
                Defaults = KeyChord.List("Ctrl+E"),
            };

            // A function key, so a text box doesn't keep it: help is for the window, wherever focus is.
            yield return new KeyAction
            {
                Id = Help, Name = SharedStrings.KeyActionHelp, Category = SharedStrings.KeyCategoryHelp, Scope = KeyScope.Global,
                Defaults = KeyChord.List("F1"), AllowBare = true,
            };
        }
    }
}
