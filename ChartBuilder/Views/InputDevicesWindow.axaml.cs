using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartPacker.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The Input devices window (charter-spec C19 item 2). While Bind waits for a control, the keys pressed here are the
    /// controls to bind, before anything else in the window takes them.
    /// </summary>
    public partial class InputDevicesWindow : Window
    {
        public InputDevicesWindow()
        {
            InitializeComponent();
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            Closed += (_, _) => ViewModel?.Dispose();
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        private InputDevicesViewModel? ViewModel => DataContext as InputDevicesViewModel;

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel is { } vm && vm.HandleKey(e.PhysicalKey, down: true))
                e.Handled = true;
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (ViewModel is { } vm && vm.HandleKey(e.PhysicalKey, down: false))
                e.Handled = true;
        }
    }
}
