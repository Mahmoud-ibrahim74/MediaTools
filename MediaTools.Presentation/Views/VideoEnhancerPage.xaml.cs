using System;
using System.Linq;
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

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[VideoEnhancerPage] Loaded");
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
        Console.WriteLine("[VideoEnhancerPage] DragLeave");
        _viewModel.IsDropHover = false;
        e.Handled = true;
    }

    private void OnEnhancerDrop(object sender, DragEventArgs e)
    {
        _viewModel.IsDropHover = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Console.WriteLine($"[VideoEnhancerPage] Drop: {paths.Length} path(s) — {string.Join(", ", paths.Take(3))}{(paths.Length > 3 ? "…" : string.Empty)}");
            _viewModel.HandleDrop(paths);
        }
        else
        {
            Console.WriteLine("[VideoEnhancerPage] Drop: no file paths");
        }

        e.Handled = true;
    }

    private void OnSourcePreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[VideoEnhancerPage] Source preview MediaOpened");
        if (sender is MediaElement me)
        {
            me.Play();
        }
    }

    private void OnSourcePreviewMediaEnded(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[VideoEnhancerPage] Source preview MediaEnded (loop)");
        if (sender is MediaElement me)
        {
            me.Position = TimeSpan.Zero;
            me.Play();
        }
    }
}
