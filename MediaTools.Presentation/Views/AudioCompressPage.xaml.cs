using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class AudioCompressPage : Page
{
    private readonly AudioCompressViewModel _viewModel;

    public AudioCompressPage(AudioCompressViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = _viewModel.IsDropHover ? DragDropEffects.Copy : DragDropEffects.None;
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
}
