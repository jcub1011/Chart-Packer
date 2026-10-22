using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>Export Song (to <c>.sng</c>): preview, location and if-exists choice. Closes with true once Export is chosen, false otherwise.</summary>
    public partial class ExportSngWindow : Window
    {
        /// <summary>The tallest the window grows, as its markup has it, before its screen caps it further.</summary>
        private readonly double _designMaxHeight;

        public ExportSngWindow()
        {
            InitializeComponent();
            _designMaxHeight = MaxHeight;
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                KeepOnScreen();
            };
            SizeChanged += (_, _) => KeepOnScreen();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ExportSngViewModel vm)
                vm.Exported += (_, _) => Close(true);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

        /// <summary>
        /// The window no taller than its screen's working area, so Export and Cancel stay reachable, and moved as little as
        /// keeps it inside that area as it grows (the metadata list opening).
        /// </summary>
        private void KeepOnScreen()
        {
            if (!IsVisible
                || FrameSize is not { } frame
                || WindowPlacement.CenterOf(this) is not { } middle
                || (WindowPlacement.ScreenOf(this, middle) ?? Screens.ScreenFromWindow(this)) is not { } screen)
                return;

            double scaling = screen.Scaling;
            var area = screen.WorkingArea;
            double chrome = Math.Max(0, frame.Height - ClientSize.Height);
            MaxHeight = Math.Min(_designMaxHeight, (area.Height / scaling) - chrome);
            Position = WindowPlacement.PlaceAt(Position, PixelSize.FromSize(frame, scaling), area);
        }
    }
}
