using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.ViewModels;

public partial class FacebookQueueItemViewModel : ObservableObject
{
    private readonly Action<FacebookQueueItemViewModel>? _onRemoveRequested;

    public Guid Id { get; } = Guid.NewGuid();

    public string Url { get; }

    public FacebookVideoFormat Format { get; }

    public string Resolution { get; }

    public string Quality { get; }

    public FacebookQueueItemViewModel(
        string url,
        FacebookVideoFormat format = FacebookVideoFormat.Mp4,
        string resolution = "Best",
        string quality = "High",
        Action<FacebookQueueItemViewModel>? onRemoveRequested = null)
    {
        Url = url;
        Format = format;
        Resolution = resolution;
        Quality = quality;
        _onRemoveRequested = onRemoveRequested;
        _title = url;
        _progressStatusText = "In Queue";
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    [ObservableProperty]
    private FacebookQueueItemStatus _status = FacebookQueueItemStatus.Queued;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private double _progressPercentDisplay;

    [ObservableProperty]
    private string _progressStatusText;

    [ObservableProperty]
    private string _downloadSpeedDisplay = string.Empty;

    [ObservableProperty]
    private string _etaDisplay = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string? _outputFilePath;

    public bool IsActive => Status is FacebookQueueItemStatus.FetchingInfo or FacebookQueueItemStatus.Downloading or FacebookQueueItemStatus.Muxing;

    public bool CanRemove => Status is not (FacebookQueueItemStatus.Downloading or FacebookQueueItemStatus.Muxing or FacebookQueueItemStatus.FetchingInfo);

    public bool HasDuration => !string.IsNullOrWhiteSpace(DurationDisplay);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnDurationDisplayChanged(string value) => OnPropertyChanged(nameof(HasDuration));

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusChanged(FacebookQueueItemStatus value)
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanRemove));
    }

    [RelayCommand]
    private void Remove()
    {
        _onRemoveRequested?.Invoke(this);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var targetPath = OutputFilePath;
        if (targetPath is not null && File.Exists(targetPath))
        {
            targetPath = Path.GetDirectoryName(targetPath);
        }

        if (!string.IsNullOrWhiteSpace(targetPath) && Directory.Exists(targetPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true
            });
        }
    }
}
