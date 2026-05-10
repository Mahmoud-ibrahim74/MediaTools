using System;
using System.Windows;
using System.Windows.Controls;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class VideoEnhancerPage : Page
{
    private readonly VideoEnhancerViewModel _viewModel;

    public VideoEnhancerPage(VideoEnhancerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnEnhancerDragOver(object sender, DragEventArgs e)
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

    private void OnEnhancerDragLeave(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        e.Handled = true;
    }

    private void OnEnhancerDrop(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.HandleDrop(paths);
        }

        e.Handled = true;
    }

    private void OnSourcePreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me)
        {
            me.Play();
        }
    }

    private void OnSourcePreviewMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me)
        {
            me.Position = TimeSpan.Zero;
            me.Play();
        }
    }
}
