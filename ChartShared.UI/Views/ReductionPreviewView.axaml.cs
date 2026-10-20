using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>A reduction's preview (<see cref="ReductionPreviewViewModel"/>). Its look is in ReductionPreviewView.axaml.</summary>
    public partial class ReductionPreviewView : UserControl
    {
        public ReductionPreviewView()
        {
            InitializeComponent();
            Highway.Scrubbed += (_, e) => (DataContext as ReductionPreviewViewModel)?.SeekTo(e.Seconds);
        }
    }

    /// <summary>
    /// The keys a window showing a reduction's preview takes for it while it's shown: Space plays or pauses, N goes to the
    /// next change and Shift+N the one before. Tunnelled, so a focused button or box takes them as that, but not while
    /// typing in a field.
    /// </summary>
    public static class ReductionPreviewKeys
    {
        /// <summary>Has <paramref name="window"/> take the keys for the preview <paramref name="preview"/> gives, when there's one shown.</summary>
        public static void Attach(Window window, Func<ReductionPreviewViewModel?> preview)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(preview);
            bool isSpaceHeld = false;

            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (preview() is not { IsExpanded: true } shown || IsTyping(window, e))
                    return;

                if (e.Key == Key.N && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
                {
                    e.Handled = true;
                    if (e.KeyModifiers == KeyModifiers.Shift)
                        shown.PreviousChangeCommand.Execute(null);
                    else
                        shown.NextChangeCommand.Execute(null);
                    return;
                }

                if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
                    return;
                e.Handled = true;
                if (isSpaceHeld)
                    return;
                isSpaceHeld = true;
                shown.PlayPauseCommand.Execute(null);
            }, RoutingStrategies.Tunnel);

            // The key-up of a Space taken as playback is taken too: a focused button clicks as Space is let go.
            window.AddHandler(InputElement.KeyUpEvent, (_, e) =>
            {
                if (e.Key != Key.Space || !isSpaceHeld)
                    return;
                isSpaceHeld = false;
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            window.Deactivated += (_, _) => isSpaceHeld = false;
        }

        // A field takes what's typed: a text box, or a number box's.
        private static bool IsTyping(Window window, KeyEventArgs e) => e.Source is TextBox || window.FocusManager?.GetFocusedElement() is TextBox;
    }
}
