using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartShared.UI.Views;

namespace ChartPacker.Views
{
    /// <summary>
    /// The Merge window. Cancel, Escape or closing the window stops a merge that's running; once a
    /// song is merged the button closes it.
    /// </summary>
    /// <remarks>
    /// The song headers are outside the rows' scroll, so they stay as the rows scroll down; they're given the rows'
    /// width and scrolled sideways with them, so each stays over its column.
    /// </remarks>
    public partial class MergeWindow : Window
    {
        private ColumnDefinition ChoicesColumn => MergeRoot.ColumnDefinitions[0];

        private ColumnDefinition PreviewColumn => MergeRoot.ColumnDefinitions[2];

        // The rows' gutter with its labels and with only its icons (the view's "gutter" widths plus their
        // 8px margins), and the chrome either side of the cells (the rows' 3px bar and the cells' 4px right
        // margin), in pixels; with one narrowest cell per source this is how wide the rows have to be to
        // stay readable.
        private const double FullGutterWidth = 168;
        private const double CompactGutterWidth = 72;
        private const double RowChrome = 7;
        // Room in a song's header for its offset box beside the unit, the link menu and Compare.
        private const double MinCellWidth = 176;

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
            WindowChrome.ShowIcon(this, WindowTitleBar);
            // The splitter position and maximized state are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreWindowState();
                FitColumns();
            };

            // Raised when the viewport changes size too, e.g. the splitter moves or the vertical bar appears.
            LeftScroll.ScrollChanged += (_, _) =>
            {
                FitColumns();
                FollowRows();
            };

            // Shift+wheel over the headers scrolls them; the rows follow, so they stay lined up. While the headers are still
            // laid out to an old width, though, an offset of the rows' they couldn't take yet was cut short to fit it: that's
            // theirs to catch up on, not the rows' to go back to.
            HeaderScroll.ScrollChanged += (_, e) =>
            {
                if (HeaderScroll.Offset.X.Equals(LeftScroll.Offset.X))
                    return;

                if (!e.ExtentDelta.X.Equals(0) || !HeaderScroll.Extent.Width.Equals(LeftScroll.Extent.Width))
                    FollowRows();
                else
                    LeftScroll.Offset = new Vector(HeaderScroll.Offset.X, LeftScroll.Offset.Y);
            };
            DataContextChanged += (_, _) =>
            {
                AttachSources();
                FitColumns();
            };
            AttachSources();
        }

        /// <summary>
        /// Gives the rows one width: the viewport's when there's room, so the columns stretch, or wide
        /// enough for one narrowest cell per source, so the panel scrolls sideways instead of squeezing. Narrow, the rows show only their icons, not their labels. A set width means everything inside is measured against it,
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

            // The headers' viewport ends where the rows' does, so both scroll as far. The rows' bars collapse over them, so
            // this is nothing unless the bars stop doing that and take a strip of their own.
            double bar = Math.Max(0, LeftScroll.Bounds.Width - viewport);
            if (!HeaderScroll.Margin.Right.Equals(bar))
                HeaderScroll.Margin = new Thickness(0, 0, bar, 0);
        }

        /// <summary>Scrolls the headers sideways with the rows, so each stays over its column.</summary>
        private void FollowRows()
        {
            if (!HeaderScroll.Offset.X.Equals(LeftScroll.Offset.X))
                HeaderScroll.Offset = new Vector(LeftScroll.Offset.X, 0);
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
            WindowChrome.UseOwnerIcon(dialog, this);
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
