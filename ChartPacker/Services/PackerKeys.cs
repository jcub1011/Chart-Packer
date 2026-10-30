using System.Collections.Generic;
using ChartPacker.Resources;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;

namespace ChartPacker.Services
{
    /// <summary>Chart Packer's actions with keys: the shared ones and its own (charter-spec §9).</summary>
    public static class PackerKeys
    {
        /// <summary>The main window, the only one with shortcuts of its own.</summary>
        public static readonly KeyScope Main = new("packer.main");

        public const string Rescan = "packer.rescan";
        public const string Search = "packer.search";

        public static KeybindCatalog Catalog() => new(Actions());

        private static IEnumerable<KeyAction> Actions()
        {
            foreach (var shared in SharedKeys.Actions())
                yield return shared;

            yield return new KeyAction
            {
                Id = Rescan, Name = Strings.KeyActionRescan, Category = Strings.KeyCategoryLibrary, Scope = Main,
                Defaults = KeyChord.List("F5"), AllowBare = true,
            };
            yield return new KeyAction
            {
                Id = Search, Name = Strings.KeyActionSearch, Category = Strings.KeyCategoryLibrary, Scope = Main,
                Defaults = KeyChord.List("Ctrl+F"),
            };
        }
    }
}
