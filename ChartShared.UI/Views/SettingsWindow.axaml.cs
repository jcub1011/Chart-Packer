using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Keybinds;
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
            // Ctrl+E focuses search, as in Visual Studio, or whatever the user has bound. The dispatcher tunnels, so a focused
            // editor can't swallow it; it comes before the panels are built, which may register keys of their own.
            var keys = new KeyDispatcher(AppKeys.Map, SharedKeys.SettingsWindow);
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
        }

        /// <summary>The program's name, which screen readers give the title bar's icon.</summary>
        public string? AppName
        {
            get => WindowTitleBar.AppName;
            set => WindowTitleBar.AppName = value;
        }

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            (DataContext as SettingsWindowViewModel)?.ApplyChanges();
            Close();
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    }
}
