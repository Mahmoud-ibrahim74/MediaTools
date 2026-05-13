using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class PhotoEnhancerPage : Page
{
    private readonly PhotoEnhancerViewModel _viewModel;
    private bool _eraserDragging;

    public PhotoEnhancerPage(PhotoEnhancerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _viewModel.IsDropHover = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e) => OnDragOver(sender, e);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.HandleDrop(paths);
        }

        e.Handled = true;
    }

    private void OnEraserMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement surface || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _eraserDragging = true;
        _viewModel.EraserPointerDown();
        surface.CaptureMouse();
        TryApplyEraser(surface, e.GetPosition(surface));
    }

    private void OnEraserMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_eraserDragging)
        {
            return;
        }

        _eraserDragging = false;
        if (sender is FrameworkElement surface)
        {
            surface.ReleaseMouseCapture();
        }

        _viewModel.EraserPointerUp();
    }

    private void OnEraserMouseMove(object sender, MouseEventArgs e)
    {
        if (!_eraserDragging || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement surface)
        {
            return;
        }

        TryApplyEraser(surface, e.GetPosition(surface));
    }

    private void OnEraserMouseLeave(object sender, MouseEventArgs e)
    {
        // Keep capture while dragging so strokes continue outside the surface edge.
    }

    private void TryApplyEraser(FrameworkElement surface, Point position)
    {
        var vw = _viewModel.ImagePixelWidth;
        var vh = _viewModel.ImagePixelHeight;
        if (vw <= 0 || vh <= 0)
        {
            return;
        }

        var aw = surface.ActualWidth;
        var ah = surface.ActualHeight;
        if (aw <= 1 || ah <= 1)
        {
            return;
        }

        var scale = Math.Min(aw / vw, ah / vh);
        var dispW = vw * scale;
        var dispH = vh * scale;
        var ox = (aw - dispW) / 2;
        var oy = (ah - dispH) / 2;

        var ix = (position.X - ox) / scale;
        var iy = (position.Y - oy) / scale;

        _viewModel.EraserPointerMove(ix, iy);
    }
}
