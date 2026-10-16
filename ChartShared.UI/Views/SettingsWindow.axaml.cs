using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartShared.UI.ViewModels.Settings;
using ChartShared.UI.Views;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// The Settings window. OK applies the staged changes and closes; Cancel (or Escape, or closing the
    /// window) drops them. Apply is bound to the view model.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);

            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                SearchBox.Focus();
            };

            // Ctrl+E focuses search, as in Visual Studio. Tunnelled, so a focused editor can't swallow it.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        }

        /// <summary>The program's name, which screen readers give the title bar's icon.</summary>
        public string? AppName
        {
            get => WindowTitleBar.AppName;
            set => WindowTitleBar.AppName = value;
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.E || e.KeyModifiers != KeyModifiers.Control)
                return;

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            (DataContext as SettingsWindowViewModel)?.ApplyChanges();
            Close();
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    }
}
