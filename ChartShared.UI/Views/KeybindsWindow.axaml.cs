using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.ViewModels.Keybinds;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// The keyboard shortcuts window. OK gives the app the shortcuts as edited here and closes; Cancel (or Escape, or closing
    /// the window) drops them. While it waits for a key to be pressed, every key is the answer, and Escape cancels the wait.
    /// </summary>
    public partial class KeybindsWindow : Window
    {
        // The key whose press was taken as an answer and hasn't come up: its key up is swallowed, as a focused button would
        // click on Space's.
        private Key? _answeredKey;

        public KeybindsWindow()
        {
            // Focus search is the key the Settings window has for it, or whatever the user has bound; the live keys, not the ones
            // being edited here. While a key is awaited every key is the answer, so none of these run.
            var keys = new KeyDispatcher(AppKeys.Map, SharedKeys.SettingsWindow)
            {
                IsBlocked = () => DataContext is KeybindsWindowViewModel { IsCapturing: true },
            };
            keys.Attach(this);
            keys.On(SharedKeys.FocusSearch, () =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            });

            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            WindowMemory.Remember(this);

            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                SearchBox.Focus();
            };

            // Tunnelled, so a focused button or box can't take a key that is meant as the new shortcut.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

            DataContextChanged += (_, _) =>
            {
                if (DataContext is KeybindsWindowViewModel vm)
                    vm.RevealRequested += OnRevealRequested;
            };
        }

        /// <summary>Scrolls the list to a shortcut jumped to from the keyboard view, once the list has been laid out with it in.</summary>
        private void OnRevealRequested(object? sender, KeybindRowViewModel row) =>
            Dispatcher.UIThread.Post(
                () => this.GetVisualDescendants()
                    .OfType<Border>()
                    .FirstOrDefault(b => b.Classes.Contains("row") && ReferenceEquals(b.DataContext, row))
                    ?.BringIntoView(),
                DispatcherPriority.Loaded);

        /// <summary>A drop-down opened to pick a shortcut for the key: its search box takes what is typed.</summary>
        private void OnAssignOpened(object? sender, EventArgs e) =>
            Dispatcher.UIThread.Post(() => AssignSearchBox.Focus(), DispatcherPriority.Input);

        /// <summary>A shortcut picked from the drop-down closes it; the view model binds the key to it.</summary>
        private void OnAssignPicked(object? sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
                AssignButton.Flyout?.Hide();
        }

        /// <summary>The program's name, which screen readers give the title bar's icon.</summary>
        public string? AppName
        {
            get => WindowTitleBar.AppName;
            set => WindowTitleBar.AppName = value;
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not KeybindsWindowViewModel vm)
                return;

            if (vm.IsCapturing)
            {
                e.Handled = true;
                _answeredKey = e.Key;
                if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                    vm.CancelCapture();
                else if (!KeyInput.HasMeta(e.KeyModifiers) && vm.CaptureAction is { } action)
                    _ = vm.CaptureAsync(action.Scope.Match == KeyMatch.Physical ? KeyInput.Physical(e) : KeyInput.Logical(e));
                return;
            }

            // Escape closes without applying, as the Cancel button does. Not a button's IsCancel, which would also take
            // the Escape that cancels a wait for a key.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (_answeredKey != e.Key)
                return;

            _answeredKey = null;
            e.Handled = true;
        }

        private async void OnOk(object? sender, RoutedEventArgs e)
        {
            // The window stays up for the message, which says the keys weren't saved, then closes either way.
            if (DataContext is KeybindsWindowViewModel vm)
                await vm.ApplyAsync();
            Close();
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    }
}
