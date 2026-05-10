using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class VideoCompressPage : Page
{
    private readonly VideoCompressViewModel _viewModel;

    public VideoCompressPage(VideoCompressViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
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

    private void OnDragEnter(object sender, System.Windows.DragEventArgs e) => OnDragOver(sender, e);

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.HandleDrop(paths);
        }

        e.Handled = true;
    }
}
