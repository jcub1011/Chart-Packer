using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ChartPacker.Services;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Merge window. Cancel, Escape or closing the window stops a merge that's running; once a
    /// song is merged the button closes it.
    /// </summary>
    public partial class MergeWindow : Window
    {
        private ColumnDefinition ChoicesColumn => MergeRoot.ColumnDefinitions[0];

        private ColumnDefinition PreviewColumn => MergeRoot.ColumnDefinitions[2];

        // The rows' gutter with its labels and with only its icons (the view's "gutter" widths plus their
        // 8px margins), and the chrome either side of the cells (the table's 1px edges, the rows' 3px bar
        // and the cells' 4px right margin), in pixels; with one narrowest cell per source this is how
        // wide the rows have to be to stay readable.
        private const double FullGutterWidth = 168;
        private const double CompactGutterWidth = 72;
        private const double RowChrome = 9;
        private const double MinCellWidth = 140;

        // The labels are hidden once the cells would be narrower than this with them shown, and shown
        // again only some way past it, so a scroll bar coming and going can't flip them back and forth.
        private const double CompactBelowCellWidth = 180;
        private const double CompactHysteresis = 24;

        private double _lastWidth = double.NaN;
        private bool _compact;
        private MergeWindowViewModel? _fitted;

        public MergeWindow()
        {
            InitializeComponent();
            // The splitter position and maximized state are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreWindowState();
                FitColumns();
            };

            // Raised when the viewport changes size too, e.g. the splitter moves or the vertical bar appears.
            LeftScroll.ScrollChanged += (_, _) => FitColumns();
            // Scrolled sideways over the headers (Shift+wheel), the rows follow.
            HeaderScroll.ScrollChanged += (_, _) =>
            {
                if (!HeaderScroll.Offset.X.Equals(LeftScroll.Offset.X))
                    LeftScroll.Offset = LeftScroll.Offset.WithX(HeaderScroll.Offset.X);
            };
            DataContextChanged += (_, _) =>
            {
                AttachSources();
                FitColumns();
            };
            AttachSources();
        }

        /// <summary>
        /// Gives the rows and the pinned headers one width: the viewport's when there's room, so the
        /// columns stretch, or wide enough for one narrowest cell per source, so the panel scrolls
        /// sideways instead of squeezing. Narrow, the rows show only their icons, not their labels. A set width means everything inside is measured against it,
        /// so long values trim and paragraphs wrap rather than widening the panel; only more sources
        /// than fit scroll it.
        /// </summary>
        private void FitColumns()
        {
            if (DataContext is not MergeWindowViewModel viewModel || viewModel.Sources.Count == 0)
                return;

            var viewport = LeftScroll.Viewport.Width;
            if (!double.IsFinite(viewport) || viewport <= 0)
                return;

            int count = viewModel.Sources.Count;
            // The panel carries the outer margins, so the fit is against what's left of the viewport.
            double available = viewport - ColumnsPanel.Margin.Left - ColumnsPanel.Margin.Right;

            // Short of room, the rows give up their labels before the songs give up width.
            double labelledCell = (available - FullGutterWidth - RowChrome) / count;
            bool compact = labelledCell < CompactBelowCellWidth + (_compact ? CompactHysteresis : 0);
            if (compact != _compact)
            {
                _compact = compact;
                Classes.Set("compact", compact);
            }

            double gutter = compact ? CompactGutterWidth : FullGutterWidth;
            double needed = gutter + RowChrome + count * MinCellWidth;
            double width = Math.Max(available, needed);
            if (!width.Equals(_lastWidth))
            {
                ColumnsPanel.Width = width;
                HeaderPanel.Width = width;
                _lastWidth = width;
            }

            // The headers have no vertical scroll bar; leaving its width free at their right lets them
            // scroll as far as the rows do, so they stay over their columns at the far end.
            double bar = Math.Max(0, LeftScroll.Bounds.Width - viewport);
            if (!bar.Equals(HeaderScroll.Margin.Right))
                HeaderScroll.Margin = new Thickness(0, 0, bar, 0);

            if (!HeaderScroll.Offset.X.Equals(LeftScroll.Offset.X))
                HeaderScroll.Offset = HeaderScroll.Offset.WithX(LeftScroll.Offset.X);
        }

        private void AttachSources()
        {
            var viewModel = DataContext as MergeWindowViewModel;
            if (ReferenceEquals(viewModel, _fitted))
                return;

            if (_fitted is not null)
            {
                _fitted.Sources.CollectionChanged -= OnSourcesChanged;
                _fitted.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _fitted = viewModel;
            if (viewModel is not null)
            {
                viewModel.Sources.CollectionChanged += OnSourcesChanged;
                // The columns become visible once loading finishes; fit them then too.
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MergeWindowViewModel.IsLoading))
                FitColumns();
        }

        private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e) => FitColumns();

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is MergeWindowViewModel viewModel)
            {
                viewModel.CloseRequested += _ => Close();
                viewModel.CompareRequested += compare => _ = ShowCompareAsync(compare);
            }
        }

        private async Task ShowCompareAsync(MergeCompareViewModel compare)
        {
            var dialog = new MergeCompareWindow { DataContext = compare };
            await dialog.ShowDialog(this);
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (DataContext is MergeWindowViewModel viewModel)
                CaptureWindowState(viewModel);

            base.OnClosing(e);
        }

        private void CaptureWindowState(MergeWindowViewModel viewModel)
        {
            viewModel.MergeWindowMaximized = WindowState == WindowState.Maximized;

            double left = ChoicesColumn.ActualWidth;
            double right = PreviewColumn.ActualWidth;
            double total = left + right;
            if (double.IsFinite(total) && total > 0)
            {
                double fraction = right / total;
                if (double.IsFinite(fraction))
                    viewModel.MergePreviewFraction = Math.Clamp(fraction, AppSettings.MinMergePreviewFraction, AppSettings.MaxMergePreviewFraction);
            }

            viewModel.OnClosed();
        }

        private void RestoreWindowState()
        {
            if (DataContext is not MergeWindowViewModel viewModel)
                return;

            if (viewModel.MergeWindowMaximized)
                WindowState = WindowState.Maximized;

            double fraction = viewModel.MergePreviewFraction;
            if (!double.IsFinite(fraction))
                return;

            fraction = Math.Clamp(fraction, AppSettings.MinMergePreviewFraction, AppSettings.MaxMergePreviewFraction);
            ChoicesColumn.Width = new GridLength(1 - fraction, GridUnitType.Star);
            PreviewColumn.Width = new GridLength(fraction, GridUnitType.Star);
        }
    }
}
