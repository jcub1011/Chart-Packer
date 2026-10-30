using System;
using System.Windows.Input;
using Avalonia.Controls;
using ChartShared.UI.Keybinds;
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
    /// The keys a window showing a reduction's preview takes for it while it's shown: by default Space plays or pauses, N goes
    /// to the next change and Shift+N the one before, or whatever the user has bound those actions to
    /// (<see cref="SharedKeys.PlayPause"/>, <see cref="SharedKeys.NextChange"/>, <see cref="SharedKeys.PreviousChange"/>).
    /// A <see cref="KeyDispatcher"/> takes them as the press tunnels, so a focused button takes them as that, but not
    /// while typing in a field.
    /// </summary>
    public static class ReductionPreviewKeys
    {
        /// <summary>
        /// Has <paramref name="window"/> take the keys for the preview <paramref name="preview"/> gives, when there's one shown.
        /// The window must not have a <see cref="KeyDispatcher"/> of its own.
        /// </summary>
        public static void Attach(Window window, Func<ReductionPreviewViewModel?> preview)
        {
            ArgumentNullException.ThrowIfNull(window);
            ArgumentNullException.ThrowIfNull(preview);

            var keys = new KeyDispatcher(AppKeys.Map, SharedKeys.ReductionPreview);
            keys.Attach(window);
            keys.On(SharedKeys.PlayPause, () => Take(preview, shown => shown.PlayPauseCommand));
            keys.On(SharedKeys.NextChange, () => Take(preview, shown => shown.NextChangeCommand));
            keys.On(SharedKeys.PreviousChange, () => Take(preview, shown => shown.PreviousChangeCommand));
        }

        // The key is the preview's while it's shown, whether or not the command can run just now, so it isn't left to a
        // focused button, which would click as the key goes.
        private static bool Take(Func<ReductionPreviewViewModel?> preview, Func<ReductionPreviewViewModel, ICommand> command)
        {
            if (preview() is not { IsExpanded: true } shown)
                return false;

            var run = command(shown);
            if (run.CanExecute(null))
                run.Execute(null);
            return true;
        }
    }
}
