using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>A difficulty's flyout (<see cref="ViewModels.IDifficultyPicker"/>). Its look is in DifficultyPicker.axaml.</summary>
    public partial class DifficultyPicker : UserControl
    {
        // Whether the flyout is open, so a picker that comes after it opened has its suggestion worked out too.
        private bool _attached;

        public DifficultyPicker()
        {
            InitializeComponent();
        }

        /// <summary>The flyout attaches its content each time it opens, so the suggestion is worked out then.</summary>
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            RefreshSuggestion();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
        }

        /// <summary>The picker can be set after the content attached, or change while it's shown.</summary>
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_attached)
                RefreshSuggestion();
        }

        private void RefreshSuggestion() => (DataContext as IDifficultyPicker)?.RefreshSuggestion();

        /// <summary>
        /// Choosing a difficulty closes the flyout. The flyout is a popup of its own, so the button it belongs to is found
        /// through the popup it's shown in.
        /// </summary>
        /// <remarks>
        /// A button raises Click before it runs its command, and closing the flyout takes the button out of the tree, which
        /// clears its bound command. So the flyout closes once the command has run.
        /// </remarks>
        private void OnChosen(object? sender, RoutedEventArgs e)
        {
            if (sender is ILogical control && control.FindLogicalAncestorOfType<Popup>() is { PlacementTarget: Button { Flyout: { } flyout } })
                Dispatcher.UIThread.Post(flyout.Hide);
        }
    }
}
