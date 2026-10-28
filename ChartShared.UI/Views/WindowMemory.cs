using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ChartShared.UI.Services;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// Opens each window as it was when it last closed: its size, and whether it was maximized or full screen, or, for a
    /// main window, where it was too. Kept in the program's settings, by the full name of the window's type, so it lasts
    /// between runs.
    /// </summary>
    /// <remarks>
    /// Every resizable window calls <see cref="Remember"/> from its constructor, so however it's opened, it's
    /// remembered. A window without position opens where its <see cref="Window.WindowStartupLocation"/> puts it, at the
    /// size saved: a dialog over its owner.
    /// </remarks>
    public static class WindowMemory
    {
        /// <summary>The most windows kept: more than there are, so a file filled some other way can't grow without end.</summary>
        public const int MaxWindows = 64;

        /// <summary>
        /// How far, in device-independent pixels, a window's normal size can be from the size it opened at and still be
        /// the same size: the screen's whole pixels round it, by less than this at any scaling from 50% up.
        /// </summary>
        public const double SizeTolerance = 2;

        // The program's settings, and how they're saved; none until the composition root says, so nothing else (a
        // test, say) can touch them.
        private static IRememberedWindows? _settings;
        private static Action? _save;

        /// <summary>
        /// Remembers windows in <paramref name="settings"/>, saving them with <paramref name="save"/> as each closes.
        /// Called once, by the composition root, before any window is made.
        /// </summary>
        /// <exception cref="InvalidOperationException">When it's already been called.</exception>
        public static void Use(IRememberedWindows settings, Action save)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(save);
            if (_settings is not null)
                throw new InvalidOperationException("WindowMemory.Use is called once, by the composition root.");

            _settings = settings;
            _save = save;
        }

        /// <summary>
        /// Opens <paramref name="window"/>, not yet shown, as it was when it last closed, and remembers how it was when it
        /// closes. Call from its constructor, after InitializeComponent and after any Closing handler that captures view
        /// state into the settings, so the one save as it closes keeps that too. Does nothing, but warn, before
        /// <see cref="Use"/>.
        /// </summary>
        /// <param name="position">Whether it opens where it was too, as a main window does, rather than where it's put.</param>
        public static void Remember(Window window, bool position = false)
        {
            string key = window.GetType().FullName ?? window.GetType().Name;
            if (_settings is not { } settings || _save is not { } save)
            {
                Trace.TraceWarning($"{key} isn't remembered: it was made before WindowMemory.Use.");
                return;
            }

            if (settings.Windows?.GetValueOrDefault(key) is { } saved)
                Apply(window, saved, position);

            // Fitted to the screen it's going to before it's shown there, so a size saved on a bigger screen doesn't
            // show at that size first.
            window.AddHandler(Window.WindowOpenedEvent, (_, _) => FitBeforeShow(window));

            // The size it's shown at, once it's fitted to its screen: one it's closed at unchanged keeps the size saved,
            // which a smaller screen fitted it down from.
            var opened = new Size(window.Width, window.Height);
            window.Opened += (_, _) =>
            {
                FitToScreen(window);
                opened = new Size(window.Width, window.Height);
            };

            // Caught on every close, even one that's then cancelled, as where it is can't be read once it's closed, and an
            // OS shutting down won't wait for an unsaved-changes prompt. A close that's cancelled is caught again next time.
            window.Closing += (_, _) =>
            {
                if (!WindowPlacement.TryCapture(window, out var state, out var normal))
                    return;

                var windows = settings.Windows ??= [];
                if (Captured(windows.GetValueOrDefault(key), state, normal, opened, position) is { } captured)
                {
                    windows[key] = captured;
                    save();
                }
            };
        }

        /// <summary>
        /// How a window that was <paramref name="saved"/> closes: in <paramref name="state"/>, at the
        /// <paramref name="normal"/> bounds it has while that's <see cref="WindowState.Normal"/>. Maximized or full screen,
        /// it keeps its normal bounds as saved. Normal, its size is kept unless it differs from the size it was
        /// <paramref name="opened"/> at, by <see cref="SizeTolerance"/> or more; with <paramref name="position"/>, its
        /// bounds are kept whole.
        /// </summary>
        /// <returns>Null when nothing's changed, or when it's minimized, which doesn't say how it was shown.</returns>
        public static SavedWindow? Captured(SavedWindow? saved, WindowState state, WindowBounds? normal, Size opened, bool position)
        {
            if (state == WindowState.Minimized)
                return null;

            var before = saved ?? new SavedWindow();
            var captured = before with
            {
                Maximized = state == WindowState.Maximized,
                FullScreen = state == WindowState.FullScreen,
            };

            if (state == WindowState.Normal && normal is { } bounds)
            {
                if (position)
                    captured = captured with { X = bounds.Position.X, Y = bounds.Position.Y };
                if (position || Resized(bounds.Size, opened))
                    captured = captured with { Width = bounds.Size.Width, Height = bounds.Size.Height };
            }

            return captured == before ? null : captured;
        }

        /// <summary>
        /// Tidies the windows saved in <paramref name="settings"/>: a size or position half saved, or a size that isn't a
        /// positive number, is dropped whole, as <see cref="WindowBounds"/> reads them; a size is clamped to
        /// <see cref="WindowBounds.MaxSize"/>; and a window with no name or nothing left to remember is dropped.
        /// </summary>
        public static void Sanitize(IRememberedWindows settings)
        {
            if (settings.Windows is not { } windows)
                return;

            var kept = new Dictionary<string, SavedWindow>();
            foreach (var (key, saved) in windows)
            {
                if (kept.Count == MaxWindows)
                    break;
                if (string.IsNullOrWhiteSpace(key) || saved is null)
                    continue;

                var size = WindowBounds.SizeFromSaved(saved.Width, saved.Height);
                bool placed = WindowBounds.FromSaved(saved.X, saved.Y, saved.Width, saved.Height) is not null;
                if (size is null && !saved.Maximized && !saved.FullScreen)
                    continue;

                kept[key] = saved with
                {
                    Width = size?.Width,
                    Height = size?.Height,
                    X = placed ? saved.X : null,
                    Y = placed ? saved.Y : null,
                };
            }

            settings.Windows = kept.Count > 0 ? kept : null;
        }

        /// <summary>Puts <paramref name="window"/>, not yet shown, at <paramref name="saved"/>'s size and state, and where it was with <paramref name="position"/>.</summary>
        private static void Apply(Window window, SavedWindow saved, bool position)
        {
            if (position && WindowBounds.FromSaved(saved.X, saved.Y, saved.Width, saved.Height) is { } bounds)
            {
                WindowPlacement.Restore(window, bounds);
            }
            else if (WindowBounds.SizeFromSaved(saved.Width, saved.Height) is { } size)
            {
                window.Width = size.Width;
                window.Height = size.Height;
            }

            if (saved.FullScreen)
                window.WindowState = WindowState.FullScreen;
            else if (saved.Maximized)
                window.WindowState = WindowState.Maximized;
        }

        /// <summary>Whether a window <paramref name="opened"/> at one size closes at <paramref name="closed"/>, a size of the user's.</summary>
        private static bool Resized(Size closed, Size opened) =>
            Math.Abs(closed.Width - opened.Width) >= SizeTolerance || Math.Abs(closed.Height - opened.Height) >= SizeTolerance;

        /// <summary>
        /// Fits <paramref name="window"/>, about to be shown, to the working area of the screen it's going to, when it's
        /// put there rather than placed already: a dialog goes over its owner, which isn't known yet, so it's taken to be
        /// the window the user is in.
        /// </summary>
        private static void FitBeforeShow(Window window)
        {
            if (window.WindowState != WindowState.Normal
                || window.WindowStartupLocation == WindowStartupLocation.Manual
                || double.IsNaN(window.Width) || double.IsNaN(window.Height))
                return;

            var active = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                .FirstOrDefault(w => w.IsActive && w != window);
            var screen = (active is null ? null : window.Screens.ScreenFromWindow(active)) ?? window.Screens.Primary;
            if (screen is not null)
                WindowPlacement.FitTo(window, screen);
        }

        /// <summary>
        /// Fits <paramref name="window"/>, just shown, to its screen's working area when it's bigger, as a size saved on a
        /// bigger screen may be, centered where it is: when <see cref="FitBeforeShow"/> took the wrong screen.
        /// </summary>
        private static void FitToScreen(Window window)
        {
            if (window.WindowState != WindowState.Normal
                || window.FrameSize is not { } frame
                || WindowPlacement.CenterOf(window) is not { } center
                || WindowPlacement.ScreenOf(window, center) is not { } screen)
                return;

            var size = PixelSize.FromSize(frame, window.RenderScaling);
            var area = screen.WorkingArea;
            if (size.Width > area.Width || size.Height > area.Height)
                WindowPlacement.CenterOn(window, center);
        }
    }
}
