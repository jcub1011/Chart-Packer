using System.Collections.Generic;
using ChartPacker.Core.Theming;

namespace ChartPacker.UI.Theming
{
    /// <summary>
    /// The resources <see cref="DynamicTheme"/> recolours, and what each is for: the app's own brushes from
    /// Theme.axaml, and the Fluent theme's per-control keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fluent's shared accent brushes (<c>SystemControlHighlightAccentBrush</c> and the rest) take
    /// <c>SystemAccentColor</c> where Fluent defines them, not where they're used, so overriding the colour under a
    /// control changes nothing. The control-level keys that alias them are overridden here instead. The names are
    /// Avalonia.Themes.Fluent 12.1.4's (<c>Accents/FluentControlResources.xaml</c>); a key the theme doesn't define
    /// is skipped.
    /// </para>
    /// <para>
    /// Left alone on purpose: anything whose colour carries meaning (difficulty tiers, warnings, invalid values,
    /// the highway) and the translucent white hover overlays, which already sit on whatever is under them.
    /// </para>
    /// </remarks>
    public static class DynamicThemeKeys
    {
        /// <summary>Brush resources, each replaced by a brush whose colour follows the theme.</summary>
        public static IReadOnlyList<KeyValuePair<string, ThemeRole>> Brushes { get; } =
        [
            // Theme.axaml: surfaces and borders.
            Neutral("AppBackgroundBrush"),
            Neutral("SurfaceBrush"),
            Neutral("ToolbarBrush"),
            Neutral("ToolbarGroupBrush"),
            Neutral("SubtleBorderBrush"),
            Neutral("ToolTipStripBrush"),
            Neutral("WaveformBrush"),

            // Theme.axaml: the accent.
            Role("AccentBrush", ThemeRole.Accent),
            Role("AccentTintBrush", ThemeRole.Accent),
            Role("AccentTintFaintBrush", ThemeRole.Accent),
            Role("AccentTintStrongBrush", ThemeRole.Accent),
            Role("AccentBorderBrush", ThemeRole.Accent),
            Role("AccentTextBrush", ThemeRole.AccentText),
            Role("AccentHighlightBrush", ThemeRole.AccentHighlight),
            Role("AccentButtonBackground", ThemeRole.Accent),
            Role("AccentButtonBackgroundPointerOver", ThemeRole.AccentHover),
            Role("AccentButtonBackgroundPressed", ThemeRole.AccentPressed),
            Neutral("AccentButtonBackgroundDisabled"),
            Role("AccentButtonForeground", ThemeRole.AccentForeground),
            Role("AccentButtonForegroundPointerOver", ThemeRole.AccentForeground),
            Role("AccentButtonForegroundPressed", ThemeRole.AccentHighlight),

            // Inputs: Theme.axaml's and Fluent's.
            Neutral("TextControlBackground"),
            Neutral("TextControlBackgroundPointerOver"),
            Neutral("TextControlBackgroundFocused"),
            Neutral("TextControlBorderBrush"),
            Neutral("TextControlBorderBrushPointerOver"),
            Role("TextControlBorderBrushFocused", ThemeRole.Accent),
            Role("TextControlSelectionHighlightColor", ThemeRole.Accent),
            Role("TextControlButtonBackgroundPressed", ThemeRole.Accent),
            Role("TextControlButtonForegroundPointerOver", ThemeRole.Accent),
            Neutral("ComboBoxBackground"),
            Neutral("ComboBoxBackgroundPointerOver"),
            Neutral("ComboBoxBackgroundPressed"),
            Neutral("ComboBoxBorderBrush"),
            Neutral("ComboBoxBorderBrushPointerOver"),
            Role("ComboBoxBackgroundUnfocused", ThemeRole.Accent),
            Role("ComboBoxItemBackgroundSelected", ThemeRole.Accent),
            Role("ComboBoxItemBackgroundSelectedPointerOver", ThemeRole.Accent),
            Role("ComboBoxItemBackgroundSelectedPressed", ThemeRole.Accent),

            // Popups: flyouts, menus, drop-downs and tooltips.
            Neutral("ComboBoxDropDownBackground"),
            Neutral("ComboBoxDropDownBorderBrush"),
            Neutral("FlyoutPresenterBackground"),
            Neutral("FlyoutBorderThemeBrush"),
            Neutral("MenuFlyoutPresenterBackground"),
            Neutral("MenuFlyoutPresenterBorderBrush"),
            Neutral("ToolTipBackground"),
            Neutral("ToolTipBorderBrush"),

            // Rows and items under the pointer or pressed: opaque greys in this palette, so tinted with the rest.
            Neutral("SystemControlHighlightListLowBrush"),
            Neutral("SystemControlHighlightListMediumBrush"),
            Neutral("ComboBoxItemBackgroundPointerOver"),
            Neutral("ComboBoxItemBackgroundPressed"),
            Neutral("MenuFlyoutItemBackgroundPointerOver"),
            Neutral("MenuFlyoutItemBackgroundPressed"),

            // Fluent's shared accent brushes, for the templates that use them directly, e.g. a selected ListBoxItem.
            // They're translucent through the brush's opacity, which is kept.
            Role("SystemControlHighlightAccentBrush", ThemeRole.Accent),
            Role("SystemControlForegroundAccentBrush", ThemeRole.Accent),
            Role("SystemControlBackgroundAccentBrush", ThemeRole.Accent),
            Role("SystemControlHighlightListAccentLowBrush", ThemeRole.Accent),
            Role("SystemControlHighlightListAccentMediumBrush", ThemeRole.Accent),
            Role("SystemControlHighlightListAccentHighBrush", ThemeRole.Accent),

            // Fluent's checked and selected states.
            Role("ToggleButtonBackgroundChecked", ThemeRole.Accent),
            Role("ToggleButtonBackgroundCheckedPointerOver", ThemeRole.AccentPressed),
            Role("SplitButtonBackgroundChecked", ThemeRole.Accent),
            Role("SplitButtonBackgroundCheckedPointerOver", ThemeRole.AccentPressed),
            Role("SplitButtonBackgroundCheckedPressed", ThemeRole.AccentHover),
            Role("CheckBoxCheckBackgroundFillChecked", ThemeRole.Accent),
            Role("CheckBoxCheckBackgroundFillCheckedPointerOver", ThemeRole.AccentHover),
            Role("CheckBoxCheckBackgroundFillCheckedPressed", ThemeRole.AccentPressed),
            Role("CheckBoxCheckBackgroundStrokeCheckedPointerOver", ThemeRole.AccentPressed),
            Role("CheckBoxCheckBackgroundStrokeCheckedPressed", ThemeRole.AccentHover),
            Role("CheckBoxCheckBackgroundFillIndeterminate", ThemeRole.Accent),
            Role("CheckBoxCheckBackgroundFillIndeterminatePointerOver", ThemeRole.AccentHover),
            Role("CheckBoxCheckBackgroundFillIndeterminatePressed", ThemeRole.AccentPressed),
            Role("CheckBoxCheckBackgroundStrokeIndeterminate", ThemeRole.Accent),
            Role("CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", ThemeRole.AccentHover),
            Role("CheckBoxCheckBackgroundStrokeIndeterminatePressed", ThemeRole.AccentPressed),
            Role("RadioButtonOuterEllipseCheckedStroke", ThemeRole.Accent),
            Role("RadioButtonOuterEllipseCheckedStrokePointerOver", ThemeRole.AccentHover),
            Role("RadioButtonOuterEllipseCheckedStrokePressed", ThemeRole.AccentPressed),
            Role("RadioButtonOuterEllipseCheckedFill", ThemeRole.Accent),
            Role("RadioButtonOuterEllipseCheckedFillPointerOver", ThemeRole.AccentHover),
            Role("RadioButtonOuterEllipseCheckedFillPressed", ThemeRole.AccentPressed),
            Role("SliderThumbBackground", ThemeRole.Accent),
            Role("SliderThumbBackgroundPointerOver", ThemeRole.AccentHover),
            Role("SliderThumbBackgroundPressed", ThemeRole.AccentPressed),
            Role("SliderTrackValueFill", ThemeRole.Accent),
            Role("SliderTrackValueFillPointerOver", ThemeRole.Accent),
            Role("SliderTrackValueFillPressed", ThemeRole.Accent),
            Role("ToggleSwitchFillOn", ThemeRole.Accent),
            Role("ToggleSwitchFillOnPointerOver", ThemeRole.AccentHover),
            Role("ToggleSwitchFillOnPressed", ThemeRole.AccentPressed),
            Role("ToggleSwitchStrokeOnPointerOver", ThemeRole.AccentHover),
            Role("ToggleSwitchStrokeOnPressed", ThemeRole.AccentPressed),
        ];

        /// <summary>
        /// <see cref="Avalonia.Media.Color"/> resources, replaced in the resource dictionary when the theme changes.
        /// They change at once; a control that wants them to fade gives the property a <c>ColorTransition</c>.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, ThemeRole>> Colors { get; } =
        [
            Role("SpectrumLowColor", ThemeRole.SpectrumLow),
            Role("SpectrumMiddleColor", ThemeRole.SpectrumMiddle),
            Role("SpectrumHighColor", ThemeRole.SpectrumHigh),
        ];

        private static KeyValuePair<string, ThemeRole> Neutral(string key) => new(key, ThemeRole.Neutral);

        private static KeyValuePair<string, ThemeRole> Role(string key, ThemeRole role) => new(key, role);
    }
}
