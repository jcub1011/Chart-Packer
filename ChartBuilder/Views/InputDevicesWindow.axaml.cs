using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The Input devices window (charter-spec C19 item 2). The keys pressed here light the controls they're bound to; while
    /// Bind waits for a control, they're the controls to bind, before anything else in the window takes them.
    /// </summary>
    public partial class InputDevicesWindow : Window
    {
        public InputDevicesWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
            Deactivated += (_, _) => ViewModel?.ReleaseKeys();
            Closed += (_, _) => ViewModel?.Dispose();
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        private InputDevicesViewModel? ViewModel => DataContext as InputDevicesViewModel;

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (ViewModel is { } vm && vm.HandleKey(e.PhysicalKey, down: true, IsTyping(e)))
                e.Handled = true;
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (ViewModel is { } vm && vm.HandleKey(e.PhysicalKey, down: false, IsTyping(e)))
                e.Handled = true;
        }

        // A device's setting being typed in, whose box keeps the keys, digits included.
        private bool IsTyping(KeyEventArgs e) => e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;
    }
}
