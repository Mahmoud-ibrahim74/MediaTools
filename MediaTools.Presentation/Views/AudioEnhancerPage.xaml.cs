using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaTools.Presentation.ViewModels;

namespace MediaTools.Presentation.Views;

public partial class AudioEnhancerPage : Page
{
    private readonly AudioEnhancerViewModel _viewModel;

    public AudioEnhancerPage(AudioEnhancerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioEnhancerViewModel.IsRunning))
        {
            return;
        }

        if (!_viewModel.IsRunning)
        {
            return;
        }

        // Export runs FFmpeg on another thread; keep preview silent during encode (no accidental playback overlap).
        Dispatcher.Invoke(() =>
        {
            try
            {
                AudioPreviewPlayer.Pause();
            }
            catch (InvalidOperationException)
            {
            }
        });
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

    private void OnAudioPreviewMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me)
        {
            me.Volume = 1.0;
            // Do not Pause() or seek here — on Windows/WMF, pausing immediately after MediaOpened
            // often leaves the graph unable to output audio when Play() is pressed later.
        }
    }

    private void OnPreviewPlayClick(object sender, RoutedEventArgs e)
    {
        if (AudioPreviewPlayer.Source is null)
        {
            return;
        }

        var me = AudioPreviewPlayer;
        me.Volume = Math.Max(me.Volume, 0.01);
        try
        {
            if (me.NaturalDuration.HasTimeSpan)
            {
                me.Position = TimeSpan.Zero;
            }
        }
        catch (InvalidOperationException)
        {
        }

        me.Play();
    }

    private void OnPreviewPauseClick(object sender, RoutedEventArgs e)
    {
        AudioPreviewPlayer.Pause();
    }

    private void OnAudioPreviewMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var detail = e.ErrorException?.Message;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = "Could not decode this file for preview.";
        }

        _viewModel.ReportPreviewPlaybackFailed(detail);
    }

    private void OnAudioPreviewMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me)
        {
            try
            {
                if (me.NaturalDuration.HasTimeSpan)
                {
                    me.Position = TimeSpan.Zero;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
