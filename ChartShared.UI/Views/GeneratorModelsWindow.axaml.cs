using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartShared.UI.Views;

namespace ChartShared.UI.Views;

/// <summary>The generator model storage dialog's window (issue #154), which both apps share.</summary>
public partial class GeneratorModelsWindow : Window
{
    public GeneratorModelsWindow()
    {
        InitializeComponent();
        WindowChrome.ShowIcon(this, WindowTitleBar);
        Opened += (_, _) => WindowChrome.UseDarkTitleBar(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
