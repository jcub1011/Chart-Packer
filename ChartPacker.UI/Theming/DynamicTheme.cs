using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ChartPacker.Core.Theming;

namespace ChartPacker.UI.Theming
{
    /// <summary>
    /// Themes a control and everything in it, popups included, from a <see cref="ThemeSeed"/>: the accent takes the
    /// seed's hue and the greys a faint tint of it. A change of seed fades over <see cref="DurationProperty"/>; a
    /// <see langword="null"/> seed fades back to the app's own colours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control gets a resource dictionary of its own, overriding the keys in <see cref="DynamicThemeKeys"/> with
    /// brushes this class recolours in place, so nothing re-resolves while a fade runs. Flyouts, context menus and
    /// tooltips find their resources through the control they open from, so they follow too. Each key's original
    /// colour is read from the app, through the control's parents, when the control first joins a window.
    /// </para>
    /// <para>
    /// The fades are run here, a frame at a time: Avalonia only runs transitions on visuals, never on brushes.
    /// </para>
    /// </remarks>
    /// <example><c>&lt;UserControl theme:DynamicTheme.Seed="{Binding ThemeSeed}"&gt;</c></example>
    public sealed class DynamicTheme
    {
        /// <summary>The seed to theme the control from, or <see langword="null"/> for the app's own colours.</summary>
        public static readonly AttachedProperty<ThemeSeed?> SeedProperty =
            AvaloniaProperty.RegisterAttached<DynamicTheme, Control, ThemeSeed?>("Seed");

        /// <summary>How long a change of seed takes to fade in.</summary>
        public static readonly AttachedProperty<TimeSpan> DurationProperty =
            AvaloniaProperty.RegisterAttached<DynamicTheme, Control, TimeSpan>("Duration", TimeSpan.FromMilliseconds(400));

        private static readonly ConditionalWeakTable<Control, Scope> Scopes = [];

        static DynamicTheme()
        {
            SeedProperty.Changed.AddClassHandler<Control>((control, _) => Scopes.GetValue(control, c => new Scope(c)).Update());
        }

        private DynamicTheme()
        {
        }

        public static ThemeSeed? GetSeed(Control element) => element.GetValue(SeedProperty);

        public static void SetSeed(Control element, ThemeSeed? value) => element.SetValue(SeedProperty, value);

        public static TimeSpan GetDuration(Control element) => element.GetValue(DurationProperty);

        public static void SetDuration(Control element, TimeSpan value) => element.SetValue(DurationProperty, value);

        private static ThemeColor ToTheme(Color color) => new(color.A, color.R, color.G, color.B);

        private static Color ToAvalonia(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

        private static double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);

        /// <summary>One themed control: its resource dictionary, and the fade in progress.</summary>
        private sealed class Scope
        {
            private readonly Control _host;
            private readonly ResourceDictionary _resources = [];
            private readonly List<ThemedBrush> _brushes = [];
            private readonly List<ThemedColor> _colors = [];
            private bool _isInstalled;
            private bool _isFrameRequested;
            private TimeSpan? _fadeStart;

            public Scope(Control host)
            {
                _host = host;
                _host.AttachedToVisualTree += (_, _) => Update();
            }

            /// <summary>Moves toward the host's current seed: at once before it has a window, else as a fade.</summary>
            public void Update()
            {
                if (!_isInstalled)
                {
                    if (TopLevel.GetTopLevel(_host) is null)
                        return;
                    Install();
                }

                var palette = GetSeed(_host) is { } seed ? ThemePalette.From(seed) : null;
                foreach (var themed in _brushes)
                {
                    themed.From = ToTheme(themed.Brush.Color);
                    themed.To = palette?.Resolve(themed.Role, themed.Original) ?? themed.Original;
                }

                foreach (var themed in _colors)
                {
                    var colour = ToAvalonia(palette?.Resolve(themed.Role, themed.Original) ?? themed.Original);
                    if (!Equals(_resources[themed.Key], colour))
                        _resources[themed.Key] = colour;
                }

                _fadeStart = null;
                RequestFrame();
            }

            // Reads each key's colour as the host would see it without the theme, then puts the theme's own brushes
            // in front: one dictionary, so the host's subtree hears of the new resources once.
            private void Install()
            {
                _isInstalled = true;
                // A window not yet shown may not have taken the app's variant yet; Fluent's keys differ by variant.
                var variant = _host.ActualThemeVariant;
                if (variant == ThemeVariant.Default && Application.Current is { } app)
                    variant = app.ActualThemeVariant;
                foreach (var (key, role) in DynamicThemeKeys.Brushes)
                {
                    if (_host.TryFindResource(key, variant, out var value) && value is ISolidColorBrush brush)
                    {
                        var themed = new ThemedBrush(new SolidColorBrush(brush.Color, brush.Opacity), role, ToTheme(brush.Color));
                        _brushes.Add(themed);
                        _resources[key] = themed.Brush;
                    }
                }

                foreach (var (key, role) in DynamicThemeKeys.Colors)
                {
                    if (_host.TryFindResource(key, variant, out var value) && value is Color color)
                    {
                        _colors.Add(new ThemedColor(key, role, ToTheme(color)));
                        _resources[key] = color;
                    }
                }

                _host.Resources.MergedDictionaries.Add(_resources);
            }

            private void RequestFrame()
            {
                if (_isFrameRequested)
                    return;
                if (TopLevel.GetTopLevel(_host) is not { } topLevel)
                {
                    // Not on screen: nothing to watch fade, so finish it.
                    Step(1);
                    return;
                }

                _isFrameRequested = true;
                topLevel.RequestAnimationFrame(OnFrame);
            }

            private void OnFrame(TimeSpan now)
            {
                _isFrameRequested = false;
                _fadeStart ??= now;
                var duration = GetDuration(_host);
                double progress = duration > TimeSpan.Zero ? Math.Clamp((now - _fadeStart.Value) / duration, 0, 1) : 1;
                Step(Ease(progress));
                if (progress < 1)
                    RequestFrame();
            }

            // Sets every brush to the point t (0 to 1) of the way from where the fade started to where it ends,
            // blended in Oklab so the colours between look like steps between the two.
            private void Step(double t)
            {
                foreach (var themed in _brushes)
                {
                    var colour = t >= 1 ? themed.To : Blend(themed.From, themed.To, t);
                    themed.Brush.Color = ToAvalonia(colour);
                }
            }

            private static ThemeColor Blend(ThemeColor from, ThemeColor to, double t)
            {
                if (from == to)
                    return to;
                var a = Oklab.FromSrgb(from);
                var b = Oklab.FromSrgb(to);
                var blend = new Oklab(a.L + ((b.L - a.L) * t), a.A + ((b.A - a.A) * t), a.B + ((b.B - a.B) * t));
                return blend.ToSrgb((byte)Math.Round(from.A + ((to.A - from.A) * t)));
            }
        }

        /// <summary>A brush the theme recolours, and the colour it had in the app's own theme.</summary>
        private sealed class ThemedBrush(SolidColorBrush brush, ThemeRole role, ThemeColor original)
        {
            public SolidColorBrush Brush { get; } = brush;

            public ThemeRole Role { get; } = role;

            public ThemeColor Original { get; } = original;

            public ThemeColor From { get; set; } = original;

            public ThemeColor To { get; set; } = original;
        }

        /// <summary>A colour resource the theme replaces, and the colour it had in the app's own theme.</summary>
        private sealed record ThemedColor(string Key, ThemeRole Role, ThemeColor Original);
    }
}
