using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MediaTools.Presentation.ViewModels;

public partial class PlaylistItemViewModel : ObservableObject
{
    private readonly Action<PlaylistItemViewModel> _downloadAction;
    private CancellationTokenSource? _downloadCts;

    public PlaylistItemViewModel(
        string title,
        string url,
        string channelName,
        TimeSpan duration,
        Action<PlaylistItemViewModel> downloadAction)
    {
        Title = title;
        Url = url;
        ChannelName = channelName;
        DurationDisplay = FormatDuration(duration);
        _downloadAction = downloadAction;
    }

    public CancellationToken GetNewToken()
    {
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        return _downloadCts.Token;
    }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private string? _channelName;

    [ObservableProperty]
    private string _durationDisplay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progressPercent;
    
    [ObservableProperty]
    private double _progressPercentDisplay;

    [ObservableProperty]
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isSuccess;

    [ObservableProperty]
    private bool _isError;
    
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void Download()
    {
        _downloadAction?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _downloadCts?.Cancel();
    }

    private bool CanDownload => !IsDownloading && !IsSuccess;
    private bool CanCancel => IsDownloading;

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"h\:mm\:ss");
        }
        return ts.ToString(@"m\:ss");
    }
}
