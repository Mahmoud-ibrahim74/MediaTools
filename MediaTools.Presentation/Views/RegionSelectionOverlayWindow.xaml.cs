using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MediaTools.Presentation.Views;

/// <summary>
/// Fullscreen semi-transparent overlay for drag-selecting a screen rectangle (Bandicam-style),
/// with optional delay and a Start recording action.
/// </summary>
public partial class RegionSelectionOverlayWindow : Window
{
    private const double MinDragPixels = 24;

    private readonly Path _dimPath = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
        IsHitTestVisible = false
    };

    private readonly Rectangle _rubberBand = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
        StrokeThickness = 2,
        Fill = Brushes.Transparent,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    private Point _dragStart;
    private bool _isDragging;
    private Rect _selection;
    private Int32Rect? _pendingScreenRect;
    private CancellationTokenSource? _countdownCts;
    private bool _isCountingDown;

    /// <summary>Region to record; set only when the dialog closes with <c>true</c> after Start recording.</summary>
    public Int32Rect? SelectedScreenRect { get; private set; }

    /// <summary>Delay (seconds) chosen in the overlay; countdown runs here before the dialog closes.</summary>
    public int SelectedDelaySeconds { get; private set; }

    public RegionSelectionOverlayWindow()
    {
        InitializeComponent();
        OverlayCanvas.Children.Add(_dimPath);
        OverlayCanvas.Children.Add(_rubberBand);

        foreach (var s in new[] { 0, 3, 5, 10, 15 })
        {
            DelayComboBox.Items.Add(s);
        }

        DelayComboBox.SelectedItem = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Keyboard.Focus(this);
        UpdateCanvasMetrics();
        UpdateDimGeometry();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCanvasMetrics();

    private void OnMainCaptureAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCanvasMetrics();

    private void UpdateCanvasMetrics()
    {
        if (OverlayCanvas is null || MainCaptureArea is null || MainCaptureArea.ActualWidth < 1 || MainCaptureArea.ActualHeight < 1)
        {
            return;
        }

        OverlayCanvas.Width = MainCaptureArea.ActualWidth;
        OverlayCanvas.Height = MainCaptureArea.ActualHeight;
        UpdateDimGeometry();
    }

    private void OnCaptureMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCountingDown)
        {
            _countdownCts?.Cancel();
            e.Handled = true;
            return;
        }

        _countdownCts?.Cancel();
        _countdownCts = null;
        CountdownStatusText.Text = string.Empty;
        SetFooterInteractive(true);

        _pendingScreenRect = null;
        StartRecordingButton.IsEnabled = false;
        SelectedScreenRect = null;

        _dragStart = e.GetPosition(MainCaptureArea);
        _isDragging = true;
        _selection = Rect.Empty;
        MainCaptureArea.CaptureMouse();
        _rubberBand.Visibility = Visibility.Collapsed;
        UpdateDimGeometry();
        e.Handled = true;
    }

    private void OnCaptureMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var p = e.GetPosition(MainCaptureArea);
        _selection = new Rect(
            Math.Min(_dragStart.X, p.X),
            Math.Min(_dragStart.Y, p.Y),
            Math.Abs(p.X - _dragStart.X),
            Math.Abs(p.Y - _dragStart.Y));

        if (_selection.Width >= 2 && _selection.Height >= 2)
        {
            _rubberBand.Visibility = Visibility.Visible;
            Canvas.SetLeft(_rubberBand, _selection.Left);
            Canvas.SetTop(_rubberBand, _selection.Top);
            _rubberBand.Width = _selection.Width;
            _rubberBand.Height = _selection.Height;
        }
        else
        {
            _rubberBand.Visibility = Visibility.Collapsed;
        }

        UpdateDimGeometry();
        e.Handled = true;
    }

    private void OnCaptureMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        MainCaptureArea.ReleaseMouseCapture();
        _isDragging = false;

        if (_selection.Width >= MinDragPixels && _selection.Height >= MinDragPixels)
        {
            var topLeft = MainCaptureArea.PointToScreen(_selection.TopLeft);
            var bottomRight = MainCaptureArea.PointToScreen(_selection.BottomRight);
            var x = (int)Math.Round(Math.Min(topLeft.X, bottomRight.X));
            var y = (int)Math.Round(Math.Min(topLeft.Y, bottomRight.Y));
            var w = (int)Math.Round(Math.Abs(bottomRight.X - topLeft.X));
            var h = (int)Math.Round(Math.Abs(bottomRight.Y - topLeft.Y));
            _pendingScreenRect = new Int32Rect(x, y, Math.Max(16, w), Math.Max(16, h));
            StartRecordingButton.IsEnabled = true;
        }
        else
        {
            _pendingScreenRect = null;
            StartRecordingButton.IsEnabled = false;
            _selection = Rect.Empty;
            _rubberBand.Visibility = Visibility.Collapsed;
        }

        UpdateDimGeometry();
        e.Handled = true;
    }

    private void OnCaptureMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCountingDown)
        {
            _countdownCts?.Cancel();
        }
        else
        {
            CancelOverlay();
        }

        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isCountingDown)
            {
                _countdownCts?.Cancel();
            }
            else
            {
                CancelOverlay();
            }

            e.Handled = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isCountingDown)
        {
            _countdownCts?.Cancel();
            return;
        }

        CancelOverlay();
    }

    private async void OnStartRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_pendingScreenRect is null)
        {
            return;
        }

        _countdownCts?.Cancel();
        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;

        var delay = DelayComboBox.SelectedItem is int d ? d : 0;

        try
        {
            _isCountingDown = true;
            SetFooterInteractive(false);
            StartRecordingButton.IsEnabled = false;

            for (var i = delay; i > 0; i--)
            {
                token.ThrowIfCancellationRequested();
                CountdownStatusText.Text = $"Recording starts in {i}s…";
                await Task.Delay(1000, token).ConfigureAwait(true);
            }

            token.ThrowIfCancellationRequested();
            CountdownStatusText.Text = "Starting…";

            SelectedDelaySeconds = delay;
            SelectedScreenRect = _pendingScreenRect;
            _isCountingDown = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _isCountingDown = false;
            try
            {
                if (IsLoaded)
                {
                    SetFooterInteractive(true);
                    StartRecordingButton.IsEnabled = _pendingScreenRect.HasValue;
                    CountdownStatusText.Text = string.Empty;
                }
            }
            catch
            {
                // window may be closing
            }
        }
    }

    private void SetFooterInteractive(bool enabled)
    {
        DelayComboBox.IsEnabled = enabled;
        CancelOverlayButton.IsEnabled = enabled;
    }

    private void CancelOverlay()
    {
        _isCountingDown = false;
        _countdownCts?.Cancel();
        _countdownCts = null;

        if (_isDragging)
        {
            MainCaptureArea.ReleaseMouseCapture();
            _isDragging = false;
        }

        _pendingScreenRect = null;
        SelectedScreenRect = null;
        SelectedDelaySeconds = 0;
        DialogResult = false;
    }

    /// <summary>
    /// Closes this overlay when recording is started from elsewhere (e.g. global Start hotkey) so the dimmed screen,
    /// delay row, and buttons do not stay visible during capture.
    /// </summary>
    public void DismissForRecordingStartedElsewhere()
    {
        try
        {
            if (!IsLoaded)
            {
                return;
            }

            CancelOverlay();
        }
        catch
        {
            try
            {
                Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <inheritdoc cref="DismissForRecordingStartedElsewhere"/>
    public static void CloseAllForRecordingStart()
    {
        if (global::System.Windows.Application.Current is null)
        {
            return;
        }

        foreach (var w in global::System.Windows.Application.Current.Windows.OfType<RegionSelectionOverlayWindow>().ToArray())
        {
            w.DismissForRecordingStartedElsewhere();
        }
    }

    private void UpdateDimGeometry()
    {
        if (MainCaptureArea is null || MainCaptureArea.ActualWidth < 1 || MainCaptureArea.ActualHeight < 1)
        {
            return;
        }

        var aw = MainCaptureArea.ActualWidth;
        var ah = MainCaptureArea.ActualHeight;
        var outer = new RectangleGeometry(new Rect(0, 0, aw, ah));

        var showHole = _selection.Width >= 2 && _selection.Height >= 2 && (_isDragging || _pendingScreenRect.HasValue);
        if (!showHole)
        {
            _dimPath.Data = outer;
            return;
        }

        var inner = new RectangleGeometry(_selection);
        _dimPath.Data = Geometry.Combine(outer, inner, GeometryCombineMode.Exclude, null);
    }
}
